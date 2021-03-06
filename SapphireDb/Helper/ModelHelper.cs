﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Newtonsoft.Json.Linq;
using SapphireDb.Attributes;
using SapphireDb.Internal;
using SapphireDb.Internal.Prefilter;
using SapphireDb.Models;

namespace SapphireDb.Helper
{
    static class ModelHelper
    {
        public static object[] GetPrimaryKeyValues(this Type type, SapphireDbContext db,
            Dictionary<string, JValue> primaryKeys)
        {
            return type.GetPrimaryKeys(db)
                .Select(p => primaryKeys[p.Name.ToCamelCase()].ToObject(p.ClrType)).ToArray();
        }

        public static object[] GetPrimaryKeyValues(this Type type, SapphireDbContext db, object entityObject)
        {
            return type.GetPrimaryKeys(db)
                .Select(p => p.PropertyInfo.GetValue(entityObject)).ToArray();
        }

        public static string[] GetPrimaryKeyNames(this Type type, SapphireDbContext db)
        {
            return type.GetPrimaryKeys(db).Select(p => p.Name.ToCamelCase()).ToArray();
        }

        private static readonly ConcurrentDictionary<Type, IProperty[]> PrimaryKeyDictionary =
            new ConcurrentDictionary<Type, IProperty[]>();

        public static IProperty[] GetPrimaryKeys(this Type type, SapphireDbContext db)
        {
            if (PrimaryKeyDictionary.TryGetValue(type, out IProperty[] primaryKeys))
            {
                return primaryKeys;
            }

            primaryKeys = db.Model.FindEntityType(type.FullName).FindPrimaryKey().Properties.ToArray();
            PrimaryKeyDictionary.TryAdd(type, primaryKeys);

            return primaryKeys;
        }

        private static readonly ConcurrentDictionary<Type, ModelAttributesInfo> ModelAttributesInfos =
            new ConcurrentDictionary<Type, ModelAttributesInfo>();

        public static ModelAttributesInfo GetModelAttributesInfo(this Type entityType)
        {
            if (ModelAttributesInfos.TryGetValue(entityType, out ModelAttributesInfo authModelInfo))
            {
                return authModelInfo;
            }

            authModelInfo = new ModelAttributesInfo(entityType);
            ModelAttributesInfos.TryAdd(entityType, authModelInfo);

            return authModelInfo;
        }

        private static readonly ConcurrentDictionary<Type, PropertyAttributesInfo[]> PropertyAttributesInfos =
            new ConcurrentDictionary<Type, PropertyAttributesInfo[]>();

        public static PropertyAttributesInfo[] GetPropertyAttributesInfos(this Type entityType)
        {
            if (PropertyAttributesInfos.TryGetValue(entityType, out PropertyAttributesInfo[] propertyInfos))
            {
                return propertyInfos;
            }

            propertyInfos = entityType.GetProperties().Select(p => new PropertyAttributesInfo(p)).ToArray();
            PropertyAttributesInfos.TryAdd(entityType, propertyInfos);

            return propertyInfos;
        }

        public static object SetFields(this Type entityType, object newValues)
        {
            object newEntityObject = Activator.CreateInstance(entityType);

            foreach (PropertyAttributesInfo pi in entityType.GetPropertyAttributesInfos()
                .Where(p => p.NonCreatableAttribute == null))
            {
                pi.PropertyInfo.SetValue(newEntityObject, pi.PropertyInfo.GetValue(newValues));
            }

            return newEntityObject;
        }

        public static void UpdateFields(this Type entityType, object entityObject, object newValues,
            SapphireDbContext db, HttpInformation information, IServiceProvider serviceProvider)
        {
            List<PropertyAttributesInfo> updatableProperties = entityType.GetPropertyAttributesInfos()
                .Where(info =>
                {
                    if (info.UpdatableAttribute != null ||
                        info.PropertyInfo.DeclaringType.GetModelAttributesInfo().UpdatableAttribute != null)
                    {
                        return info.CanUpdate(information, entityObject, serviceProvider);
                    }

                    return false;
                })
                .ToList();

            foreach (PropertyAttributesInfo pi in updatableProperties)
            {
                pi.PropertyInfo.SetValue(entityObject, pi.PropertyInfo.GetValue(newValues));
            }
        }

        public static IQueryable<object> GetValues(this SapphireDbContext db, KeyValuePair<Type, string> property,
            IServiceProvider serviceProvider, HttpInformation httpInformation)
        {
            IQueryable<object> values = (IQueryable<object>) db.GetType().GetProperty(property.Value)?.GetValue(db);
            values = values?.AsNoTracking();

            QueryFunctionAttribute queryFunctionAttribute =
                property.Key.GetModelAttributesInfo().QueryFunctionAttribute;
            if (queryFunctionAttribute != null)
            {
                if (queryFunctionAttribute.FunctionBuilder != null)
                {
                    values = values?.Where(queryFunctionAttribute.GetLambda(httpInformation, property.Key));
                }
                else if (queryFunctionAttribute.FunctionInfo != null)
                {
                    object[] methodParameters =
                        queryFunctionAttribute.FunctionInfo.CreateParameters(httpInformation, serviceProvider);
                    Expression queryFunctionExpression =
                        (Expression) queryFunctionAttribute.FunctionInfo.Invoke(null, methodParameters);

                    MethodInfo whereMethodInfo = ReflectionMethodHelper.GetGenericWhere(property.Key);
                    values = (IQueryable<object>) whereMethodInfo?.Invoke(values,
                        new object[] {values, queryFunctionExpression});
                }
            }

            return values;
        }

        public static void ExecuteHookMethods<T>(this Type modelType, ModelStoreEventAttributeBase.EventType eventType,
            object newValue, HttpInformation httpInformation, IServiceProvider serviceProvider)
            where T : ModelStoreEventAttributeBase
        {
            ModelAttributesInfo modelAttributesInfo = modelType.GetModelAttributesInfo();

            List<T> eventAttributes = null;

            if (typeof(T) == typeof(CreateEventAttribute))
            {
                eventAttributes = modelAttributesInfo.CreateEventAttributes.Cast<T>().ToList();
            }
            else if (typeof(T) == typeof(UpdateEventAttribute))
            {
                eventAttributes = modelAttributesInfo.UpdateEventAttributes.Cast<T>().ToList();
            }
            else if (typeof(T) == typeof(RemoveEventAttribute))
            {
                eventAttributes = modelAttributesInfo.RemoveEventAttributes.Cast<T>().ToList();
            }

            if (eventAttributes == null)
            {
                return;
            }

            foreach (T attribute in eventAttributes)
            {
                if (eventType == ModelStoreEventAttributeBase.EventType.Before)
                {
                    if (attribute.BeforeLambda != null)
                    {
                        attribute.BeforeLambda(newValue, httpInformation);
                    }
                    else if (attribute.BeforeFunction != null)
                    {
                        attribute.BeforeFunction.Invoke(newValue,
                            attribute.BeforeFunction.CreateParameters(httpInformation, serviceProvider));
                    }
                }
                else if (eventType == ModelStoreEventAttributeBase.EventType.BeforeSave)
                {
                    if (attribute.BeforeSaveLambda != null)
                    {
                        attribute.BeforeSaveLambda(newValue, httpInformation);
                    }
                    else if (attribute.BeforeSaveFunction != null)
                    {
                        attribute.BeforeSaveFunction.Invoke(newValue,
                            attribute.BeforeSaveFunction.CreateParameters(httpInformation, serviceProvider));
                    }
                }
                else if (eventType == ModelStoreEventAttributeBase.EventType.After)
                {
                    if (attribute.AfterLambda != null)
                    {
                        attribute.AfterLambda(newValue, httpInformation);
                    }
                    else if (attribute.AfterFunction != null)
                    {
                        attribute.AfterFunction.Invoke(newValue,
                            attribute.AfterFunction.CreateParameters(httpInformation, serviceProvider));
                    }
                }
            }
        }
    }
}