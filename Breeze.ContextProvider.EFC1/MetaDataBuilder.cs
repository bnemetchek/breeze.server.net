﻿using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Breeze.ContextProvider.EFC1
{
    public static partial class EFContextProviderUtils
    {
        public static String GetMetadataFromDbContext(DbContext dbContext)
        {
            #region Init
            var metaInit = @"
			{
				""metadataVersion"": ""1.0.5"",
				""namingConvention"": ""noChange"",
				""localQueryComparisonOptions"": ""caseInsensitiveSQL"",
			}
			";

            Func<string, string> className = fullClassName =>
            {
                return fullClassName.Split('.').Last();
            };

            var name = className(dbContext.GetType().Name);
            var nameSpace = name + "Model";
            var converter = new ExpandoObjectConverter();
            dynamic meta = JsonConvert.DeserializeObject<ExpandoObject>(metaInit, converter);
            #endregion

            var entTypes = dbContext.Model.GetEntityTypes().OrderBy(et => et.Name).ToList();

            #region structuralTypes
            var structuralTypes = new List<object>();
            meta.structuralTypes = structuralTypes;
            entTypes.ForEach(entType =>
            {
                dynamic resourceEntityType = new ExpandoObject();
                structuralTypes.Add(resourceEntityType);
                resourceEntityType.shortName = className(entType.Name);
                (resourceEntityType as IDictionary<string, object>)["namespace"] = entType.ClrType.Namespace;
                resourceEntityType.autoGeneratedKeyType = entType.GetKeys().SelectMany(k => k.Properties).Any(p => p.ValueGenerated != ValueGenerated.Never) ? "Identity" : "None";
                resourceEntityType.defaultResourceName = className(entType.Name);

                var dataProperties = new List<object>();
                resourceEntityType.dataProperties = dataProperties;
                entType.GetProperties().ToList().ForEach(entProp =>
                {
                    if (resourceEntityType.shortName == "CategoryTypeLookup" && entProp.Name == "LastUpdate")
                    {
                    }
                    dynamic dataProp = new ExpandoObject();
                    dataProperties.Add(dataProp);
                    dataProp.name = entProp.Name;
                    dynamic customDataProp = new ExpandoObject();
                    //dataProp.custom = customDataProp;
                    var propType = entProp.ClrType.IsGenericType && entProp.ClrType.GetGenericTypeDefinition() == typeof(Nullable<>) ? entProp.ClrType.GenericTypeArguments[0] : entProp.ClrType;
                    dataProp.dataType = propType.Name;
                    if (entProp.IsStoreGeneratedAlways || entProp.GetAnnotations().FirstOrDefault(a => a.Name == "Relational:ColumnType" && a.Value is string && a.Value as string == "timestamp") != null)
                        dataProp.concurrencyMode = "Fixed";
                    else
                    {
                        dataProp.isNullable = entProp.IsColumnNullable();
                        if (!entProp.IsColumnNullable())
                            dataProp.defaultValue = entProp.Scaffolding().DefaultValue;
                    }
                    dataProp.isPartOfKey = entProp.IsKey();
                    if (entProp.GetMaxLength().HasValue)
                        dataProp.maxLength = entProp.GetMaxLength().Value;

                    var validators = new List<object>();
                    if (entProp.IsStoreGeneratedAlways)
                    {

                    }
                    else
                    {
                        if (!entProp.IsColumnNullable() && !entProp.IsStoreGeneratedAlways)
                            validators.Add(new { name = "required" });

                        var validatorName = "none";
                        var propTypeName = propType.Name;
                        if (propType.BaseType.Name == "Enum") propTypeName = "Enum";
                        switch (propTypeName)
                        {
                            case "String":
                                validatorName = String.Empty;
                                break;
                            case "Int64":
                            case "Int32":
                            case "Int16":
                            case "Guid":
                            case "Byte":
                                validatorName = propTypeName.ToLower();
                                break;
                            case "Decimal":
                            case "Double":
                            case "Single":
                                validatorName = "number";
                                break;
                            case "Boolean":
                                validatorName = "bool";
                                break;
                            case "DateTime":
                            case "DateTimeOffset":
                                validatorName = "date";
                                break;
                            case "Binary":
                                // TODO: don't quite know how to validate this yet.
                                break;
                            case "Time":
                                validatorName = "duration";
                                break;
                            case "Byte[]":
                                dataProp.dataType = "Binary";
                                validatorName = "none";
                                if (entProp.GetAnnotations().Count(a => a.Value as string == "timestamp") > 0)
                                {
                                    dataProp.maxLength = 8; // SQL Server identity
                                }
                                break;
                            case "Enum":
                                validatorName = "string";
                                break;
                            default:
                                break;
                        }


                        FieldInfo minValueField = entProp.ClrType.GetField("MinValue", BindingFlags.Public | BindingFlags.Static);
                        FieldInfo maxValueField = entProp.ClrType.GetField("MaxValue", BindingFlags.Public | BindingFlags.Static);
                        if (minValueField != null || maxValueField != null)
                        {
                            validators.Add(new
                            {
                                name = validatorName,
                                min = minValueField != null ? minValueField.GetValue(null) : 0,
                                max = maxValueField != null ? maxValueField.GetValue(null) : 0
                            });
                        }
                        else
                        if (!String.IsNullOrWhiteSpace(validatorName))
                        {
                            validators.Add(new
                            {
                                name = validatorName
                            });
                        }

                        // TODO: get precision and scale for numerics
                        var colType = entProp.Scaffolding().ColumnType;
                        //if (entProp.Scaffolding().ColumnType == "")

                        if (entProp.GetMaxLength().HasValue)
                            validators.Add(new { name = "maxLength", maxLength = entProp.GetMaxLength().Value });
                    }
                    // include any validators
                    if (validators.Count > 0)
                        dataProp.validators = validators;

                    // enum
                    if (propType.BaseType.Name == "Enum")
                    {
                        dataProp.enumType = "Edm." + propType.FullName;
                    }
                });


                var navs = entType.GetNavigations().ToList();
                var navigationProperties = new List<object>();
                resourceEntityType.navigationProperties = navigationProperties;
                navs.ForEach(nav =>
                {
                    dynamic navProp = new ExpandoObject();
                    navigationProperties.Add(navProp);
                    navProp.name = nav.Name;
                    navProp.entityTypeName = className(nav.GetTargetType().Name) + ":#" + nav.GetTargetType().ClrType.Namespace;
                    navProp.isScalar = !nav.IsCollection();
                    if (nav.IsDependentToPrincipal())
                    {
                        navProp.associationName = "FK_" + className(entType.Name) + "_" + className(nav.GetTargetType().Name) + "_" + nav.Name;
                        navProp.foreignKeyNames = nav.ForeignKey.Properties.Select(p => p.Name);
                    }
                    else
                    {
                        navProp.associationName = "FK_" + className(nav.GetTargetType().Name) + "_" + className(entType.Name) + "_" + nav.Name;
                        navProp.invForeignKeyNames = nav.ForeignKey.Properties.Select(p => p.Name);
                    }
                });

            });
            #endregion

            #region resourceEntityTypeMap
            dynamic resourceEntityTypeMap = new ExpandoObject();
            entTypes.ForEach(et => (resourceEntityTypeMap as IDictionary<string, object>)[className(et.Name)] = className(et.Name) + ":#" + et.ClrType.Namespace);
            meta.resourceEntityTypeMap = resourceEntityTypeMap;
            #endregion


            var finalJson = JsonConvert.SerializeObject(meta, converter);

            return finalJson;
        }
    }

    public static class ExpandoHelpers
    {
        public static void AddProperty(this ExpandoObject expando, string propertyName, object propertyValue)
        {
            // ExpandoObject supports IDictionary so we can extend it like this
            var expandoDict = expando as IDictionary<string, object>;
            if (expandoDict.ContainsKey(propertyName))
                expandoDict[propertyName] = propertyValue;
            else
                expandoDict.Add(propertyName, propertyValue);
        }

        public static bool IsValid(this ExpandoObject expando, string propertyName)
        {
            // Check that they supplied a name
            if (string.IsNullOrWhiteSpace(propertyName))
                return false;
            // ExpandoObject supports IDictionary so we can extend it like this
            var expandoDict = expando as IDictionary<string, object>;
            return expandoDict.ContainsKey(propertyName);

        }
    }
}