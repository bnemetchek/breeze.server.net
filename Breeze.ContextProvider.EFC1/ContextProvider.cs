﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Configuration;
using System.Data;
using System.Data.Common;


using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Infrastructure;


using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;


using Breeze.ContextProvider;
using BreezeCp = Breeze.ContextProvider;
using System.Dynamic;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Breeze.ContextProvider.EFC1
{

    public interface IEFContextProvider
    {
        String GetEntitySetName(Type entityType);
    }

    // T is either a subclass of DbContext or a subclass of ObjectContext
    public partial class EFContextProvider<T> : Breeze.ContextProvider.ContextProvider, IEFContextProvider where T : DbContext, new()
    {

        public EFContextProvider()
        {

        }

        //[Obsolete("The contextName is no longer needed. This overload will be removed after Dec 31st 2012.")]
        //public EFContextProvider(string contextName)
        //{

        //}

        public T Context
        {
            get
            {
                if (_context == null)
                {
                    _context = CreateContext();
                    // Disable lazy loading and proxy creation as this messes up the data service.
                    // BJN note: these do not exist in EFC1
                    //dbCtx.Configuration.ProxyCreationEnabled = false;
                    //dbCtx.Configuration.LazyLoadingEnabled = false;
                }
                return _context;
            }
        }

        protected virtual T CreateContext()
        {
            return new T();
        }


        /// <summary>Gets the EntityConnection from the ObjectContext.</summary>
        public DbConnection EntityConnection
        {
            get
            {
                return (DbConnection)GetDbConnection();
            }
        }

        /// <summary>Gets the StoreConnection from the ObjectContext.</summary>
        public DbConnection StoreConnection
        {
            get
            {
                return Context.Database.GetDbConnection();
            }
        }

        /// <summary>Gets the current transaction, if one is in progress.</summary>
        public IDbTransaction EntityTransaction
        {
            get; private set;
        }


        /// <summary>Gets the EntityConnection from the ObjectContext.</summary>
        public override IDbConnection GetDbConnection()
        {
            return Context.Database.GetDbConnection();
        }

        /// <summary>
        /// Opens the DbConnection used by the Context.
        /// If the connection will be used outside of the DbContext, this method should be called prior to DbContext 
        /// initialization, so that the connection will already be open when the DbContext uses it.  This keeps
        /// the DbContext from closing the connection, so it must be closed manually.
        /// See http://blogs.msdn.com/b/diego/archive/2012/01/26/exception-from-dbcontext-api-entityconnection-can-only-be-constructed-with-a-closed-dbconnection.aspx
        /// </summary>
        /// <returns></returns>
        protected override void OpenDbConnection()
        {
            if (Context.Database.GetDbConnection().State == ConnectionState.Closed)
                Context.Database.OpenConnection();
        }

        protected override void CloseDbConnection()
        {
            if (_context != null)
            {
                var ec = _context.Database.GetDbConnection();
                ec.Close();
                ec.Dispose();
            }
        }

        // Override BeginTransaction so we can keep the current transaction in a property
        protected override IDbTransaction BeginTransaction(System.Data.IsolationLevel isolationLevel)
        {
            var conn = GetDbConnection();
            if (conn == null) return null;
            EntityTransaction = conn.BeginTransaction(isolationLevel);
            return EntityTransaction;
        }


        #region Base implementation overrides

        protected override string BuildJsonMetadata()
        {
            var json = GetMetadataFromContext(Context);
            return json;
        }

        public static String GetMetadataFromContext(DbContext context)
        {
            return EFContextProviderUtils.GetMetadataFromDbContext(context);
        }


        protected override EntityInfo CreateEntityInfo()
        {
            return new EFEntityInfo();
        }

        public override object[] GetKeyValues(EntityInfo entityInfo)
        {
            return GetKeyValues(entityInfo.Entity);
        }

        public object[] GetKeyValues(object entity)
        {

            var entType = Context.Model.GetEntityTypes().Where(et => et.ClrType.GetType() == entity.GetType()).FirstOrDefault();
            if (entType == null)
            {
                throw new ArgumentException("EntitySet not found for type " + entity.GetType());
            }

            var entry = Context.Entry(entity);
            if (entry == null)
                throw new ArgumentException("Entity not in context " + entity.GetType());
            var primaryKey = entry.Metadata.FindPrimaryKey();
            var keyNames = primaryKey.Properties.Select(k => k.Name).ToList();
            var keyValues = keyNames.Select(keyName => entry.Property(keyName).CurrentValue).ToArray();
            return keyValues;
        }

        protected override void SaveChangesCore(SaveWorkState saveWorkState)
        {
            var saveMap = saveWorkState.SaveMap;
            var deletedEntities = ProcessSaves(saveMap);

            if (deletedEntities.Any())
            {
                ProcessAllDeleted(deletedEntities);
            }
            ProcessAutogeneratedKeys(saveWorkState.EntitiesWithAutoGeneratedKeys);
            ProcessIdentityKeys(saveWorkState.EntitiesWithAutoGeneratedKeys);

            int count;
            try
            {
                count = Context.SaveChanges();
            }
            catch (DbUpdateException e)
            {
                var entityErrors = new List<EntityError>();
                foreach (var entry in e.Entries)
                {
                    var entity = entry.Entity;
                    var entityTypeName = entity.GetType().FullName;
                    Object[] keyValues;
                    var primaryKey = entry.Metadata.FindPrimaryKey();
                    if (primaryKey != null && entry.IsKeySet)
                    {
                        var keyNames = primaryKey.Properties.Select(k => k.Name).ToList();
                        keyValues = keyNames.Select(keyName => entry.Property(keyName).CurrentValue).ToArray();
                    }
                    else
                    {
                        var entityInfo = saveWorkState.EntitiesWithAutoGeneratedKeys.FirstOrDefault(ei => ei.Entity == entity);
                        if (entityInfo != null)
                        {
                            keyValues = new Object[] { entityInfo.AutoGeneratedKey.TempValue };
                        }
                        else
                        {
                            // how can this happen?
                            keyValues = null;
                        }
                    }

                    // EF Core 1 does not have validation errors
                    //foreach (var ve in eve.ValidationErrors)
                    //{
                    //	var entityError = new EntityError()
                    //	{
                    //		EntityTypeName = entityTypeName,
                    //		KeyValues = keyValues,
                    //		ErrorMessage = ve.ErrorMessage,
                    //		PropertyName = ve.PropertyName
                    //	};
                    //	entityErrors.Add(entityError);
                    //}

                }
                saveWorkState.EntityErrors = entityErrors;

            }
            catch (DataException e)
            {
                var nextException = (Exception)e;
                while (nextException.InnerException != null)
                {
                    nextException = nextException.InnerException;
                }
                if (nextException == e)
                {
                    throw;
                }
                else
                {
                    //create a new exception that contains the toplevel exception
                    //but has the innermost exception message propogated to the top.
                    //For EF exceptions, this is often the most 'relevant' message.
                    throw new Exception(nextException.Message, e);
                }
            }
            catch (Exception e)
            {
                throw;
            }

            saveWorkState.KeyMappings = UpdateAutoGeneratedKeys(saveWorkState.EntitiesWithAutoGeneratedKeys);
        }




        #endregion

        #region Save related methods

        private List<EFEntityInfo> ProcessSaves(Dictionary<Type, List<EntityInfo>> saveMap)
        {
            var deletedEntities = new List<EFEntityInfo>();
            foreach (var kvp in saveMap)
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;  // skip GetEntitySetName if no entities
                var entityType = kvp.Key;
                var entitySetName = GetEntitySetName(entityType);
                foreach (EFEntityInfo entityInfo in kvp.Value)
                {
                    // entityInfo.EFContextProvider = this;  may be needed eventually.
                    entityInfo.EntitySetName = entitySetName;
                    ProcessEntity(entityInfo);
                    if (entityInfo.EntityState == BreezeCp.EntityState.Deleted)
                    {
                        deletedEntities.Add(entityInfo);
                    }
                }
            }
            return deletedEntities;
        }

        private void ProcessAllDeleted(List<EFEntityInfo> deletedEntities)
        {
            deletedEntities.ForEach(entityInfo =>
            {
                RestoreOriginal(entityInfo);
                var entry = GetOrAddEntityEntry(entityInfo);
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Deleted;
                entityInfo.EntityEntry = entry;
            });
        }

        private void ProcessAutogeneratedKeys(List<EntityInfo> entitiesWithAutoGeneratedKeys)
        {
            var tempKeys = entitiesWithAutoGeneratedKeys.Cast<EFEntityInfo>().Where(
              entityInfo => entityInfo.AutoGeneratedKey.AutoGeneratedKeyType == AutoGeneratedKeyType.KeyGenerator)
              .Select(ei => new TempKeyInfo(ei))
              .ToList();
            if (tempKeys.Count == 0) return;
            if (this.KeyGenerator == null)
            {
                this.KeyGenerator = GetKeyGenerator();
            }
            this.KeyGenerator.UpdateKeys(tempKeys);
            tempKeys.ForEach(tki =>
            {
                // Clever hack - next 3 lines cause all entities related to tki.Entity to have 
                // their relationships updated. So all related entities for each tki are updated.
                // Basically we set the entity to look like a preexisting entity by setting its
                // entityState to unchanged.  This is what fixes up the relations, then we set it back to added
                // Now when we update the pk - all fks will get changed as well.  Note that the fk change will only
                // occur during the save.
                var entry = GetEntityEntry(tki.Entity);
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Added;
                var val = ConvertValue(tki.RealValue, tki.Property.PropertyType);
                tki.Property.SetValue(tki.Entity, val, null);
            });
        }

        private void ProcessIdentityKeys(List<EntityInfo> entitiesWithAutoGeneratedKeys)
        {
            var tempKeys = entitiesWithAutoGeneratedKeys.Cast<EFEntityInfo>().Where(
              entityInfo => entityInfo.AutoGeneratedKey.AutoGeneratedKeyType == AutoGeneratedKeyType.Identity)
              .Select(ei => new TempKeyInfo(ei))
              .ToList();
            if (tempKeys.Count == 0) return;
            // temp key values must be zero for EF core to use the identity mechanism
            tempKeys.ForEach(tki =>
            {
                var entry = GetEntityEntry(tki.Entity);
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Added;
                var val = ConvertValue(tki.RealValue, tki.Property.PropertyType);
                tki.Property.SetValue(tki.Entity, val, null);
            });
        }


        private IKeyGenerator GetKeyGenerator()
        {
            var generatorType = KeyGeneratorType.Value;
            return (IKeyGenerator)Activator.CreateInstance(generatorType, StoreConnection);
        }

        private EntityInfo ProcessEntity(EFEntityInfo entityInfo)
        {
            EntityEntry ose;
            if (entityInfo.EntityState == BreezeCp.EntityState.Modified)
            {
                ose = HandleModified(entityInfo);
            }
            else if (entityInfo.EntityState == BreezeCp.EntityState.Added)
            {
                ose = HandleAdded(entityInfo);
            }
            else if (entityInfo.EntityState == BreezeCp.EntityState.Deleted)
            {
                // for 1st pass this does NOTHING 
                ose = HandleDeletedPart1(entityInfo);
            }
            else
            {
                // needed for many to many to get both ends into the objectContext
                ose = HandleUnchanged(entityInfo);
            }
            entityInfo.EntityEntry = ose;
            return entityInfo;
        }

        private EntityEntry HandleAdded(EFEntityInfo entityInfo)
        {
            var entry = AddEntityEntry(entityInfo);
            if (entityInfo.AutoGeneratedKey != null)
            {
                entityInfo.AutoGeneratedKey.TempValue = GetOsePropertyValue(entry, entityInfo.AutoGeneratedKey.PropertyName);
            }
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Added;
            return entry;
        }

        private EntityEntry HandleModified(EFEntityInfo entityInfo)
        {
            var entry = AddEntityEntry(entityInfo);
            // EntityState will be changed to modified during the update from the OriginalValuesMap
            // Do NOT change this to EntityState.Modified because this will cause the entire record to update.

            entry.State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;

            // updating the original values is necessary under certain conditions when we change a foreign key field
            // because the before value is used to determine ordering.
            UpdateOriginalValues(entry, entityInfo);

            //foreach (var dep in GetModifiedComplexTypeProperties(entity, metadata)) {
            //  entry.SetModifiedProperty(dep.Name);
            //}

            if (entry.State != Microsoft.EntityFrameworkCore.EntityState.Modified || entityInfo.ForceUpdate)
            {
                // _originalValusMap can be null if we mark entity.SetModified but don't actually change anything.
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Modified;
            }
            return entry;
        }

        private EntityEntry HandleUnchanged(EFEntityInfo entityInfo)
        {
            var entry = AddEntityEntry(entityInfo);
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;
            return entry;
        }

        private EntityEntry HandleDeletedPart1(EntityInfo entityInfo)
        {
            return null;
        }

        private EntityInfo RestoreOriginal(EntityInfo entityInfo)
        {
            // fk's can get cleared depending on the order in which deletions occur -
            // EF needs the original values of these fk's under certain circumstances - ( not sure entirely what these are). 
            // so we restore the original fk values right before we attach the entity 
            // shouldn't be any side effects because we delete it immediately after.
            // ??? Do concurrency values also need to be restored in some cases 
            // This method restores more than it actually needs to because we don't
            // have metadata easily avail here, but usually a deleted entity will
            // not have much in the way of OriginalValues.
            if (entityInfo.OriginalValuesMap == null || entityInfo.OriginalValuesMap.Keys.Count == 0)
            {
                return entityInfo;
            }
            var entity = entityInfo.Entity;
            var entityType = entity.GetType();
            //var efEntityType = GetEntityType(Context.MetadataWorkspace, entityType);
            //var keyPropertyNames = efEntityType.KeyMembers.Select(km => km.Name).ToList();
            var keyPropertyNames = Context.Entry(entity).Metadata.GetKeys().ToList().SelectMany(k => k.Properties).Select(k => k.Name);
            var ovl = entityInfo.OriginalValuesMap.ToList();
            for (var i = 0; i < ovl.Count; i++)
            {
                var kvp = ovl[i];
                var propName = kvp.Key;
                // keys should be ignored
                if (keyPropertyNames.Contains(propName)) continue;
                var pi = entityType.GetProperty(propName);
                // unmapped properties should be ignored.
                if (pi == null) continue;
                var nnPropType = TypeFns.GetNonNullableType(pi.PropertyType);
                // presumption here is that only a predefined type could be a fk or concurrency property
                if (TypeFns.IsPredefinedType(nnPropType))
                {
                    SetPropertyValue(entity, propName, kvp.Value);
                }
            }

            return entityInfo;
        }

        //private static EntityType GetEntityType(MetadataWorkspace mws, Type entityType)
        //{
        //	EntityType et =
        //	  mws.GetItems<EntityType>(DataSpace.OSpace)
        //		.Single(x => x.Name == entityType.Name);
        //	return et;
        //}

        private static void UpdateOriginalValues(EntityEntry entry, EntityInfo entityInfo)
        {
            var originalValuesMap = entityInfo.OriginalValuesMap;
            if (originalValuesMap == null || originalValuesMap.Keys.Count == 0) return;

            var entity = entry.Context.Entry(entry.Entity);
            originalValuesMap.ToList().ForEach(kvp =>
            {
                var propertyName = kvp.Key;
                var originalValue = kvp.Value;
                var prop = entity.Property(kvp.Key);

                try
                {
                    prop.IsModified = true;
                    if (originalValue is JObject)
                    {
                        // only really need to perform updating original values on key properties
                        // and a complex object cannot be a key.
                    }
                    else
                    {
                        var fieldType = entity.Metadata.FindProperty(propertyName).ClrType;
                        var originalValueConverted = ConvertValue(originalValue, fieldType);

                        prop.OriginalValue = originalValueConverted;
                    }
                }
                catch (Exception e)
                {
                    if (e.Message.Contains(" part of the entity's key"))
                    {
                        throw;
                    }
                    else
                    {
                        // this can happen for "custom" data entity properties.
                    }
                }
            });

        }

        private List<KeyMapping> UpdateAutoGeneratedKeys(List<EntityInfo> entitiesWithAutoGeneratedKeys)
        {
            // where clause is necessary in case the Entities were suppressed in the beforeSave event.
            var keyMappings = entitiesWithAutoGeneratedKeys.Cast<EFEntityInfo>()
              .Where(entityInfo => entityInfo.EntityEntry != null)
              .Select(entityInfo =>
              {
                  var autoGeneratedKey = entityInfo.AutoGeneratedKey;
                  if (autoGeneratedKey.AutoGeneratedKeyType == AutoGeneratedKeyType.Identity)
                  {
                      autoGeneratedKey.RealValue = GetOsePropertyValue(entityInfo.EntityEntry, autoGeneratedKey.PropertyName);
                  }
                  return new KeyMapping()
                  {
                      EntityTypeName = entityInfo.Entity.GetType().FullName,
                      TempValue = autoGeneratedKey.TempValue,
                      RealValue = autoGeneratedKey.RealValue
                  };
              });
            return keyMappings.ToList();
        }

        private Object GetOsePropertyValue(EntityEntry ose, String propertyName)
        {
            return Context.Entry(ose.Entity).Property(propertyName).CurrentValue;
        }

        private void SetOsePropertyValue(EntityEntry ose, String propertyName, Object value)
        {
            Context.Entry(ose.Entity).Property(propertyName).CurrentValue = value;
        }

        private void SetPropertyValue(Object entity, String propertyName, Object value)
        {
            var propInfo = entity.GetType().GetProperty(propertyName,
                                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // exit if unmapped property.
            if (propInfo == null) return;
            if (propInfo.CanWrite)
            {
                var val = ConvertValue(value, propInfo.PropertyType);
                propInfo.SetValue(entity, val, null);
            }
            else
            {
                throw new Exception(String.Format("Unable to write to property '{0}' on type: '{1}'", propertyName,
                                                  entity.GetType()));
            }
        }

        private static Object ConvertValue(Object val, Type toType)
        {
            Object result;
            if (val == null) return val;
            if (toType == val.GetType()) return val;
            var nnToType = TypeFns.GetNonNullableType(toType);
            if (typeof(IConvertible).IsAssignableFrom(nnToType))
            {
                result = Convert.ChangeType(val, nnToType, System.Threading.Thread.CurrentThread.CurrentCulture);
            }
            else if (val is JObject)
            {
                var serializer = new JsonSerializer();
                result = serializer.Deserialize(new JTokenReader((JObject)val), toType);
            }
            else
            {
                // Guids fail above - try this
                TypeConverter typeConverter = TypeDescriptor.GetConverter(toType);
                if (typeConverter.CanConvertFrom(val.GetType()))
                {
                    result = typeConverter.ConvertFrom(val);
                }
                else if (val is DateTime && toType == typeof(DateTimeOffset))
                {
                    // handle case where JSON deserializes to DateTime, but toType is DateTimeOffset.  DateTimeOffsetConverter doesn't work!
                    result = new DateTimeOffset((DateTime)val);
                }
                else
                {
                    result = val;
                }
            }
            return result;
        }

        private EntityEntry GetOrAddEntityEntry(EFEntityInfo entityInfo)
        {
            var entry = Context.ChangeTracker.Entries().Where(e => e.Entity == entityInfo.Entity).FirstOrDefault();
            if (entry != null) return entry;

            return AddEntityEntry(entityInfo);
        }

        private EntityEntry AddEntityEntry(EFEntityInfo entityInfo)
        {
            var ose = GetEntityEntry(entityInfo.Entity, false);
            if (ose != null) return ose;
            Context.Add(entityInfo.Entity);
            // Attach has lots of side effect - add has far fewer.
            return GetEntityEntry(entityInfo);
        }

        private EntityEntry AttachEntityEntry(EFEntityInfo entityInfo)
        {
            Context.Attach(entityInfo.Entity);
            // Attach has lots of side effect - add has far fewer.
            return GetEntityEntry(entityInfo);
        }

        private EntityEntry GetEntityEntry(EFEntityInfo entityInfo)
        {
            return GetEntityEntry(entityInfo.Entity);
        }

        private EntityEntry GetEntityEntry(Object entity, bool errorIfNotFound = true)
        {
            var entry = Context.ChangeTracker.Entries().Where(e => e.Entity == entity).FirstOrDefault();
            if (entry == null && errorIfNotFound)
            {
                throw new Exception("unable to add to context: " + entity);
            }
            return entry;
        }


        #endregion
        // TODO: may want to improve perf on this later ( cache the mappings maybe).
        public String GetEntitySetName(Type entityType)
        {
            var entType = Context.Model.GetEntityTypes().Where(et => et.ClrType == entityType).FirstOrDefault();
            if (entType != null)
                return entType.Name;
            else
                return "";
        }

        private const string ResourcePrefix = @"res://";

        private T _context;
    }




    public class EFEntityInfo : EntityInfo
    {
        internal EFEntityInfo()
        {
        }

        internal String EntitySetName;
        internal EntityEntry EntityEntry;
    }

    public class EFEntityError : EntityError
    {
        public EFEntityError(EntityInfo entityInfo, String errorName, String errorMessage, String propertyName)
        {


            if (entityInfo != null)
            {
                this.EntityTypeName = entityInfo.Entity.GetType().FullName;
                this.KeyValues = GetKeyValues(entityInfo);
            }
            ErrorName = errorName;
            ErrorMessage = errorMessage;
            PropertyName = propertyName;
        }

        private Object[] GetKeyValues(EntityInfo entityInfo)
        {
            return entityInfo.ContextProvider.GetKeyValues(entityInfo);
        }

    }


}