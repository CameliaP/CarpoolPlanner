﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CarpoolPlanner
{
    public class KeyedCollectionConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return IsSubclassOfGenericType(objectType, typeof(KeyedCollection<,>));
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var obj = JObject.Load(reader);
            var collection = existingValue as IList;
            var listType = objectType.GetInterface("System.Collections.Generic.IList`1");
            var itemType = listType.GetGenericArguments().First();
            foreach (var property in obj.Properties())
            {
                var item = property.Value.ToObject(itemType);
                collection.Add(item);
            }
            return collection;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var collection = value as IList;
            writer.WriteStartObject();
            var objectType = value.GetType();
            var listType = objectType.GetInterface("System.Collections.Generic.IList`1");
            var itemType = listType.GetGenericArguments().First();
            var getKeyMethod = objectType.GetMethod("GetKeyForItem", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { itemType }, null);
            foreach (var item in collection)
            {
                var key = getKeyMethod.Invoke(value, new object[] { item });
                if (key == null)
                    continue;
                writer.WritePropertyName(key.ToString());
                serializer.Serialize(writer, item);
            }
            writer.WriteEndObject();
        }

        private static bool IsSubclassOfGenericType(Type objectType, Type genericType)
        {
            while (objectType != null && objectType != typeof(object))
            {
                var cur = objectType.IsGenericType ? objectType.GetGenericTypeDefinition() : objectType;
                if (genericType == cur)
                    return true;
                objectType = objectType.BaseType;
            }
            return false;
        }
    }
}