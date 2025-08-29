#region Header
/**
 * JsonMapper.cs
 *   JSON to .Net object and object to JSON conversions.
 *
 * The authors disclaim copyright to this source code. For more details, see
 * the COPYING file included with this distribution.
 **/
#endregion


using Best.HTTP.Shared.PlatformSupport.Threading;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;


namespace Best.HTTP.JSON.LitJson
{
    public interface IFastSetter
    {
        void SetBool(ReadOnlySpan<char> property, bool value);
        void SetInteger(ReadOnlySpan<char> property, long value);
        void SetDouble(ReadOnlySpan<char> property, double value);
        void SetOther(ReadOnlySpan<char> property, object value);
    }

    class ReadOnlyMemoryCharEqualityComparer : IEqualityComparer<ReadOnlyMemory<char>>
    {
        public static ReadOnlyMemoryCharEqualityComparer Comparer = new ReadOnlyMemoryCharEqualityComparer();

        public bool Equals(ReadOnlyMemory<char> x, ReadOnlyMemory<char> y)
        {
            var eq = MemoryExtensions.Equals(x.Span, y.Span, StringComparison.OrdinalIgnoreCase);
            return eq;
        }

        public int GetHashCode(ReadOnlyMemory<char> obj)
        {
            HashCode hash = new HashCode();

            for (int i = 0; i < obj.Length; ++i)
                hash.Add(Char.ToLowerInvariant(obj.Span[i]));

            return hash.ToHashCode();
        }
    }

    internal struct PropertyMetadata
    {
        public MemberInfo Info;
        public bool       IsField;
        public Type       Type;
    }


    internal struct ArrayMetadata
    {
        private Type element_type;
        private bool is_array;
        private bool is_list;


        public Type ElementType
        {
            get
            {
                if (element_type == null)
                    return typeof (JsonData);

                return element_type;
            }

            set { element_type = value; }
        }

        public bool IsArray
        {
            get { return is_array; }
            set { is_array = value; }
        }

        public bool IsList
        {
            get { return is_list; }
            set { is_list = value; }
        }
    }


    internal struct ObjectMetadata
    {
        private Type element_type;
        private bool is_dictionary;

        private IDictionary<ReadOnlyMemory<char>, PropertyMetadata> properties;


        public Type ElementType
        {
            get
            {
                if (element_type == null)
                    return typeof (JsonData);

                return element_type;
            }

            set { element_type = value; }
        }

        public bool IsDictionary
        {
            get { return is_dictionary; }
            set { is_dictionary = value; }
        }

        public IDictionary<ReadOnlyMemory<char>, PropertyMetadata> Properties
        {
            get { return properties; }
            set { properties = value; }
        }
    }


    internal delegate void ExporterFunc    (object obj, JsonWriter writer);
    public   delegate void ExporterFunc<T> (T obj, JsonWriter writer);

    internal delegate object ImporterFunc                (object input);
    public   delegate TValue ImporterFunc<TJson, TValue> (TJson input);

    public delegate IJsonWrapper WrapperFactory ();


    public sealed class JsonMapper
    {
        #region Fields
        private static readonly int max_nesting_depth;

        private static readonly IFormatProvider datetime_format;

        private static readonly IDictionary<Type, ExporterFunc> base_exporters_table;
        private static readonly IDictionary<Type, ExporterFunc> custom_exporters_table;

        private static readonly IDictionary<Type,
                IDictionary<Type, ImporterFunc>> base_importers_table;
        private static readonly IDictionary<Type,
                IDictionary<Type, ImporterFunc>> custom_importers_table;

        private static readonly IDictionary<Type, ArrayMetadata> array_metadata;
        private static readonly ReaderWriterLockSlim array_metadata_lock = new();

        private static readonly IDictionary<Type,
                Dictionary<Type, MethodInfo>> conv_ops;
        private static readonly ReaderWriterLockSlim conv_ops_lock = new();

        private static readonly IDictionary<Type, ObjectMetadata> object_metadata;
        private static readonly ReaderWriterLockSlim object_metadata_lock = new();

        private static readonly IDictionary<Type,
                List<PropertyMetadata>> type_properties;
        private static readonly ReaderWriterLockSlim type_properties_lock = new();

        [ThreadStatic]
        private static JsonWriter static_writer;

        private static ConcurrentStack<JsonReader> _readerPool = new ConcurrentStack<JsonReader>();

        private static ConcurrentDictionary<ReadOnlyMemory<char>, string> _stringInstanceCache = new ConcurrentDictionary<ReadOnlyMemory<char>, string>(ReadOnlyMemoryCharEqualityComparer.Comparer);

        [ThreadStatic]
        private static char[] _tmpProperty;

        private static Type int_type;
        private static Type long_type;
        private static Type ulong_type;
        private static Type bool_type;
        private static Type double_type;
        private static Type string_type;
        private static Type nullable_type;
        #endregion


        #region Constructors
        static JsonMapper ()
        {
            max_nesting_depth = 100;

            array_metadata = new Dictionary<Type, ArrayMetadata> ();
            conv_ops = new Dictionary<Type, Dictionary<Type, MethodInfo>>();
            object_metadata = new Dictionary<Type, ObjectMetadata> ();
            type_properties = new Dictionary<Type,
                            List<PropertyMetadata>>();

            datetime_format = DateTimeFormatInfo.InvariantInfo;

            base_exporters_table   = new Dictionary<Type, ExporterFunc> ();
            custom_exporters_table = new Dictionary<Type, ExporterFunc> ();

            base_importers_table = new Dictionary<Type,
                                 IDictionary<Type, ImporterFunc>> ();
            custom_importers_table = new Dictionary<Type,
                                   IDictionary<Type, ImporterFunc>> ();

            int_type = typeof(int);
            long_type = typeof(long);
            ulong_type = typeof(ulong);
            bool_type = typeof(bool);
            double_type = typeof(double);
            string_type = typeof(string);
            nullable_type = typeof(Nullable<>);

            RegisterBaseExporters ();
            RegisterBaseImporters ();
        }
        #endregion

        #region Private Helper Mehtods

        private static bool HasInterface(Type type, string name)
        {
#if NET_4_6
            Type[] interfaces = type.GetInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
#else
            return type.GetInterface(name, true) != null;
#endif
        }

        public static PropertyInfo[] GetPublicInstanceProperties(Type type)
        {
            return type.GetProperties();
        }

        #endregion

        #region Private Methods
        private static ArrayMetadata AddArrayMetadata(Type type)
        {
            ArrayMetadata data = default;
            if (array_metadata.TryGetValue(type, out data))
                return data;

            data = new ArrayMetadata();

            data.IsArray = type.IsArray;
            data.IsList = HasInterface(type, "System.Collections.IList");

            foreach (PropertyInfo p_info in GetPublicInstanceProperties(type))
            {
                if (p_info.Name != "Item")
                    continue;

                ParameterInfo[] parameters = p_info.GetIndexParameters ();

                if (parameters.Length != 1)
                    continue;

                if (parameters[0].ParameterType == int_type)
                    data.ElementType = p_info.PropertyType;
            }

            using (new WriteLock(array_metadata_lock))
            {
                try
                {
                    array_metadata.Add (type, data);
                }
                catch (ArgumentException)
                {
                    // ??
                }
            }

            return data;
        }

        private static ObjectMetadata AddObjectMetadata(Type type)
        {
            ObjectMetadata data;
            if (object_metadata.TryGetValue(type, out data))
                return data;

            data = new ObjectMetadata();
            data.IsDictionary = HasInterface(type, "System.Collections.IDictionary");

            data.Properties = new Dictionary<ReadOnlyMemory<char>, PropertyMetadata>(ReadOnlyMemoryCharEqualityComparer.Comparer);

            foreach (PropertyInfo p_info in GetPublicInstanceProperties(type))
            {
                if (p_info.Name == "Item")
                {
                    ParameterInfo[] parameters = p_info.GetIndexParameters ();

                    if (parameters.Length != 1)
                        continue;

                    if (parameters[0].ParameterType == string_type)
                        data.ElementType = p_info.PropertyType;

                    continue;
                }

                PropertyMetadata p_data = new PropertyMetadata ();
                p_data.Info = p_info;
                p_data.Type = p_info.PropertyType;

                data.Properties.Add(p_info.Name.AsMemory(), p_data);
            }

            foreach (FieldInfo f_info in type.GetFields())
            {
                if (!data.Properties.TryGetValue(f_info.Name.AsMemory(), out var p_data))
                {
                    p_data = new PropertyMetadata();
                p_data.Info = f_info;
                p_data.IsField = true;
                p_data.Type = f_info.FieldType;

                    data.Properties.Add(f_info.Name.AsMemory(), p_data);
            }
            }

            using (new WriteLock(object_metadata_lock))
            {
                try
                {
                    object_metadata.Add (type, data);
                }
                catch (ArgumentException)
                {
                    // ??
                }
            }

            return data;
        }

        private static List<PropertyMetadata> AddTypeProperties(Type type)
        {
            List<PropertyMetadata> props;
            if (type_properties.TryGetValue(type, out props))
                return props;

            props = new List<PropertyMetadata>();

            foreach (PropertyInfo p_info in GetPublicInstanceProperties(type))
            {
                if (p_info.Name == "Item")
                    continue;

                PropertyMetadata p_data = new PropertyMetadata ();
                p_data.Info = p_info;
                p_data.IsField = false;
                props.Add (p_data);
            }

            foreach (FieldInfo f_info in type.GetFields())
            {
                PropertyMetadata p_data = new PropertyMetadata ();
                p_data.Info = f_info;
                p_data.IsField = true;

                props.Add (p_data);
            }

            using (new WriteLock(type_properties_lock))
            {
                try
                {
                    type_properties.Add (type, props);
                }
                catch (ArgumentException)
                {
                    // ??
            }
        }

            return props;
        }

        private static MethodInfo GetConvOp (Type t1, Type t2)
        {
            Dictionary<Type, MethodInfo> ops;
            using (new WriteLock(conv_ops_lock))
            {
                if (!conv_ops.TryGetValue(t1, out ops))
                    conv_ops.Add(t1, ops = new Dictionary<Type, MethodInfo>());
            }

            MethodInfo op;
            if (conv_ops[t1].TryGetValue(t2, out op))
                return op;

            op = t1.GetMethod("op_Implicit", new Type[] { t2 });

            using (new WriteLock(conv_ops_lock))
            {
                try
                {
                    conv_ops[t1].Add (t2, op);
                }
                catch (ArgumentException)
                {
                    return conv_ops[t1][t2];
                }
            }

            return op;
        }

        private static bool TryReadInt(Type inst_type, JsonReader reader, out int value)
        {
            value = 0;

            if (reader.Token != JsonToken.Int)
                return false;

            value = reader.ValueInt;
            return true;
        }

        private static bool TryReadLong(Type inst_type, JsonReader reader, out long value)
        {
            value = 0;

            if (reader.Token != JsonToken.Long)
                return false;

            value = reader.ValueLong;
            return true;
        }

        private static bool TryReadULong(Type inst_type, JsonReader reader, out ulong value)
        {
            value = 0;

            if (reader.Token != JsonToken.ULong)
                return false;

            value = reader.ValueULong;
            return true;
        }

        private static bool TryReadDouble(Type inst_type, JsonReader reader, out double value)
        {
            value = 0;

            if (reader.Token != JsonToken.Double)
                return false;

            value = reader.ValueDouble;
            return true;
        }

        private static bool TryReadBool(Type inst_type, JsonReader reader, out bool value)
        {
            value = false;

            if (reader.Token != JsonToken.Boolean)
                return false;

            value = reader.ValueBool;
            return true;
        }

        private static unsafe object ReadValue(Type inst_type, JsonReader reader, bool skipRead = false)
        {
            if (!skipRead)
            reader.Read ();

            if (reader.Token == JsonToken.ArrayEnd)
                return null;

            Type underlying_type = inst_type.IsGenericType && inst_type.GetGenericTypeDefinition() == nullable_type ? Nullable.GetUnderlyingType(inst_type) : null;
            Type value_type = underlying_type ?? inst_type;

            if (reader.Token == JsonToken.Null)
            {
                if (inst_type.IsClass || underlying_type != null)
                    return null;

                throw new JsonException(String.Format(
                        "Can't assign null to an instance of type {0}",
                        inst_type));
            }

            if (reader.Token == JsonToken.Double ||
                reader.Token == JsonToken.Int ||
                reader.Token == JsonToken.Long ||
                reader.Token == JsonToken.ULong ||
                reader.Token == JsonToken.String ||
                reader.Token == JsonToken.Boolean)
            {
                object value = null;
                Type json_type = null;// reader.Value.GetType();
                switch (reader.Token)
                {
                    case JsonToken.Int: json_type = int_type; value = reader.ValueInt; break;
                    case JsonToken.Long: json_type = long_type; value = reader.ValueLong; break;
                    case JsonToken.ULong: json_type = ulong_type; value = reader.ValueULong; break;
                    case JsonToken.Boolean: json_type = bool_type; value = reader.ValueBool; break;
                    case JsonToken.Double: json_type = double_type; value = reader.ValueDouble; break;
                    case JsonToken.String: json_type = string_type; value = GetStringInstance(reader.ValueString); break;
                }

                if (value_type.IsAssignableFrom(json_type))
                    return value;

                // If there's a custom importer that fits, use it
                if (custom_importers_table.TryGetValue(json_type, out var typeTable) &&
                    typeTable.TryGetValue(value_type, out var cutomImporter))
                {
                    return cutomImporter(value);
                }

                // Maybe there's a base importer that works
                if (base_importers_table.TryGetValue(json_type, out var importersTable) &&
                    importersTable.TryGetValue(value_type, out var importersImporter))
                {
                    return importersImporter(value);
                }

                // Maybe it's an enum
                if (value_type.IsEnum)
                {
                    if (reader.Token == JsonToken.String)
                        return Enum.Parse(value_type, value.ToString(), true);

                    return Enum.ToObject(value_type, value);
                }

                // Try using an implicit conversion operator
                MethodInfo conv_op = GetConvOp (value_type, json_type);

                if (conv_op != null)
                    return conv_op.Invoke(null, new object[] { value });

                // No luck
                throw new JsonException($"Can't assign value '{value}' (type {json_type}) to type {inst_type}");
            }

            object instance = null;

            if (reader.Token == JsonToken.ArrayStart)
            {

                //if (inst_type.FullName == "System.Object")
                if (inst_type == typeof(System.Object))
                    inst_type = typeof(object[]);

                ArrayMetadata t_data = AddArrayMetadata(inst_type);

                if (! t_data.IsArray && ! t_data.IsList)
                    throw new JsonException (String.Format (
                            "Type {0} can't act as an array",
                            inst_type));

                IList list;
                Type elem_type;

                if (!t_data.IsArray)
                {
                    //list = (IList)Activator.CreateInstance(inst_type);
                    list = (IList)GetInstance(inst_type);
                    elem_type = t_data.ElementType;
                }
                else
                {
                    list = new System.Collections.Generic.List<object>();
                    elem_type = inst_type.GetElementType ();
                }

                list.Clear();

                while (true)
                {
                    object item = ReadValue (elem_type, reader);
                    if (item == null && reader.Token == JsonToken.ArrayEnd)
                        break;

                    list.Add (item);
                }

                if (t_data.IsArray)
                {
                    int n = list.Count;
                    instance = Array.CreateInstance (elem_type, n);

                    for (int i = 0; i < n; i++)
                        ((Array) instance).SetValue (list[i], i);
                }
                else
                    instance = list;

            }
            else if (reader.Token == JsonToken.ObjectStart)
            {

                if (inst_type == typeof(System.Object))
                    value_type = inst_type = typeof(Dictionary<string, object>);

                ObjectMetadata t_data = AddObjectMetadata(value_type);

                //instance = Activator.CreateInstance(value_type);
                instance = GetInstance(value_type);
                var fastSetter = instance as IFastSetter;

                while (true)
                {
                    reader.Read ();

                    if (reader.Token == JsonToken.ObjectEnd)
                        break;

                    if (fastSetter != null)
                    {
                        if (_tmpProperty == null || _tmpProperty.Length < reader.ValueString.Length)
                            _tmpProperty = new char[reader.ValueString.Length];

                        reader.ValueString.CopyTo(_tmpProperty);

                        Span<char> prop = stackalloc char[reader.ValueString.Length];
                        _tmpProperty.AsSpan(0, reader.ValueString.Length).CopyTo(prop);

                        reader.Read();

                        switch (reader.Token)
                        {
                            case JsonToken.Int:
                                if (TryReadInt(inst_type, reader, out var value_int))
                                    fastSetter.SetInteger(prop, value_int);
                                break;

                            case JsonToken.Long:
                                if (TryReadLong(inst_type, reader, out var value_long))
                                    fastSetter.SetInteger(prop, value_long);
                                break;

                            case JsonToken.ULong:
                                if (TryReadULong(inst_type, reader, out var value_ulong))
                                {
                                    if (value_ulong > (ulong)long.MaxValue)
                                        throw new JsonException($"Can't call SetInteger for property '{new string(prop)}', because value_long({value_ulong}) is larger than long.MaxValue!");

                                    fastSetter.SetInteger(prop, (long)value_ulong);
                                }
                                break;

                            case JsonToken.Double:
                                if (TryReadDouble(inst_type, reader, out var value_double))
                                    fastSetter.SetDouble(prop, value_double);
                                break;

                            case JsonToken.Boolean:
                                if (TryReadBool(inst_type, reader, out var value_bool))
                                    fastSetter.SetBool(prop, value_bool);
                                break;

                            case JsonToken.ObjectStart:
                                reader.Read();
                                goto default;

                            default:
                                if (t_data.Properties.TryGetValue(_tmpProperty.AsMemory(0, prop.Length), out var prop_meta_data))
                                {
                                    fastSetter.SetOther(prop, ReadValue(prop_meta_data.Type, reader, reader.Token != JsonToken.ObjectStart));
                                }
                                else
                                    ReadSkip(reader);

                                break;
                        }
                    }
                    else if (!t_data.IsDictionary && t_data.Properties.TryGetValue(reader.ValueString, out var prop_data))
                    {
                        try
                        {
                            if (prop_data.IsField)
                            {
                                ((FieldInfo)prop_data.Info).SetValue(
                                    instance, ReadValue(prop_data.Type, reader));
                            }
                            else
                            {
                                PropertyInfo p_info =
                                    (PropertyInfo)prop_data.Info;

                                if (p_info.CanWrite)
                                    p_info.SetValue(
                                        instance,
                                        ReadValue(prop_data.Type, reader),
                                        null);
                                else
                                    ReadValue(prop_data.Type, reader);
                            }
                        }
                        catch(JsonException ex)
                        {
                            throw new JsonException($"While parsing property '{new string(reader.ValueString.Span)}': {ex.Message}");
                        }

                    }
                    else
                    {
                        if (!t_data.IsDictionary)
                        {

                            if (!reader.SkipNonMembers)
                            {
                                throw new JsonException (String.Format (
                                        "The type {0} doesn't have the " +
                                        "property '{1}'",
                                        inst_type, new string(reader.ValueString.Span)));
                            }
                            else
                            {
                                ReadSkip (reader);
                                continue;
                            }
                        }

                        try
                        {
                            ((IDictionary)instance).Add(
                                GetStringInstance(reader.ValueString), ReadValue(
                                    t_data.ElementType, reader));
                        }
                        catch (JsonException ex)
                        {
                            throw new JsonException($"While parsing property '{new string(reader.ValueString.Span)}': {ex.Message}");
                        }
                    }

                }

            }

            return instance;
        }

        private static IJsonWrapper ReadValue (WrapperFactory factory,
                                               JsonReader reader)
        {
            reader.Read ();

            if (reader.Token == JsonToken.ArrayEnd ||
                reader.Token == JsonToken.Null)
                return null;

            IJsonWrapper instance = factory?.Invoke();

            if (reader.Token == JsonToken.String)
            {
                instance?.SetString(new string(reader.ValueString.Span));
                return instance;
            }

            if (reader.Token == JsonToken.Double)
            {
                instance?.SetDouble(reader.ValueDouble);
                return instance;
            }

            if (reader.Token == JsonToken.Int)
            {
                instance?.SetInt(reader.ValueInt);
                return instance;
            }

            if (reader.Token == JsonToken.Long)
            {
                instance?.SetLong(reader.ValueLong);
                return instance;
            }

            if (reader.Token == JsonToken.Boolean)
            {
                instance?.SetBoolean(reader.ValueBool);
                return instance;
            }

            if (reader.Token == JsonToken.ArrayStart)
            {
                instance?.SetJsonType(JsonType.Array);

                while (true)
                {
                    IJsonWrapper item = ReadValue (factory, reader);
                    if (item == null && reader.Token == JsonToken.ArrayEnd)
                        break;

                    if (instance != null)
                    ((IList) instance).Add (item);
                }
            }
            else if (reader.Token == JsonToken.ObjectStart)
            {
                instance?.SetJsonType(JsonType.Object);

                while (true)
                {
                    reader.Read ();

                    if (reader.Token == JsonToken.ObjectEnd)
                        break;

                    string property = new string(reader.ValueString.Span);

                    if (instance != null)
                        ((IDictionary)instance)[property] = ReadValue(factory, reader);
                }

            }

            return instance;
        }

        private static void ReadSkip (JsonReader reader)
        {
            ToWrapper(null, reader);
        }

        private static void RegisterBaseExporters ()
        {
            base_exporters_table[typeof (byte)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write (Convert.ToInt32 ((byte) obj));
                };

            base_exporters_table[typeof (char)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write (Convert.ToString ((char) obj));
                };

            base_exporters_table[typeof (DateTime)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write (Convert.ToString ((DateTime) obj,
                                                    datetime_format));
                };

            base_exporters_table[typeof (decimal)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write ((decimal) obj);
                };

            base_exporters_table[typeof (sbyte)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write (Convert.ToInt32 ((sbyte) obj));
                };

            base_exporters_table[typeof (short)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write (Convert.ToInt32 ((short) obj));
                };

            base_exporters_table[typeof (ushort)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write (Convert.ToInt32 ((ushort) obj));
                };

            base_exporters_table[typeof (uint)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write (Convert.ToUInt64 ((uint) obj));
                };

            base_exporters_table[typeof (ulong)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write ((ulong) obj);
                };

            base_exporters_table[typeof(DateTimeOffset)] =
                delegate (object obj, JsonWriter writer)
                {
                    writer.Write(((DateTimeOffset)obj).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", datetime_format));
                };
        }

        private static void RegisterBaseImporters ()
        {
            ImporterFunc importer;

            importer = delegate (object input)
            {
                return Convert.ToByte ((int) input);
            };
            RegisterImporter (base_importers_table, typeof (int),
                              typeof (byte), importer);

            importer = delegate (object input)
            {
                return Convert.ToUInt64 ((int) input);
            };
            RegisterImporter (base_importers_table, typeof (int),
                              typeof (ulong), importer);

            importer = delegate (object input)
            {
                return Convert.ToInt64((int)input);
            };
            RegisterImporter(base_importers_table, typeof(int),
                              typeof(long), importer);

            importer = delegate (object input)
            {
                return Convert.ToSByte ((int) input);
            };
            RegisterImporter (base_importers_table, typeof (int),
                              typeof (sbyte), importer);

            importer = delegate (object input)
            {
                return Convert.ToInt16 ((int) input);
            };
            RegisterImporter (base_importers_table, typeof (int),
                              typeof (short), importer);

            importer = delegate (object input)
            {
                return Convert.ToUInt16 ((int) input);
            };
            RegisterImporter (base_importers_table, typeof (int),
                              typeof (ushort), importer);

            importer = delegate (object input)
            {
                return Convert.ToUInt32 ((int) input);
            };
            RegisterImporter (base_importers_table, typeof (int),
                              typeof (uint), importer);

            importer = delegate (object input)
            {
                return Convert.ToSingle ((int) input);
            };
            RegisterImporter (base_importers_table, typeof (int),
                              typeof (float), importer);

            importer = delegate (object input)
            {
                return Convert.ToDouble ((int) input);
            };
            RegisterImporter (base_importers_table, typeof (int),
                              typeof (double), importer);

            importer = delegate (object input)
            {
                return Convert.ToDecimal ((double) input);
            };
            RegisterImporter (base_importers_table, typeof (double),
                              typeof (decimal), importer);

            importer = delegate (object input)
            {
                return Convert.ToSingle((double)input);
            };
            RegisterImporter(base_importers_table, typeof(double),
                typeof(float), importer);

            importer = delegate (object input)
            {
                return Convert.ToUInt32 ((long) input);
            };
            RegisterImporter (base_importers_table, typeof (long),
                              typeof (uint), importer);

            importer = delegate (object input)
            {
                return Convert.ToChar ((string) input);
            };
            RegisterImporter (base_importers_table, typeof (string),
                              typeof (char), importer);

            importer = delegate (object input)
            {
                return Convert.ToDateTime ((string) input, datetime_format);
            };
            RegisterImporter (base_importers_table, typeof (string),
                              typeof (DateTime), importer);

            importer = delegate (object input)
            {
                return DateTimeOffset.Parse((string)input, datetime_format);
            };
            RegisterImporter(base_importers_table, typeof(string),
                typeof(DateTimeOffset), importer);
        }

        private static void RegisterImporter (
            IDictionary<Type, IDictionary<Type, ImporterFunc>> table,
            Type json_type, Type value_type, ImporterFunc importer)
        {
            if (!table.TryGetValue(json_type, out var importerTable))
                table.Add(json_type, importerTable = new Dictionary<Type, ImporterFunc>());

            importerTable[value_type] = importer;
        }

        private static void WriteValue (object obj, JsonWriter writer,
                                        bool writer_is_private,
                                        int depth)
        {
            if (depth > max_nesting_depth)
                throw new JsonException (
                    String.Format ("Max allowed object depth reached while " +
                                   "trying to export from type {0}",
                                   obj.GetType ()));

            if (obj == null)
            {
                writer.Write (null);
                return;
            }

            if (obj is IJsonWrapper)
            {
                if (writer_is_private)
                    writer.TextWriter.Write (((IJsonWrapper) obj).ToJson ());
                else
                    ((IJsonWrapper) obj).ToJson (writer);

                return;
            }

            if (obj is String)
            {
                writer.Write ((string) obj);
                return;
            }

            if (obj is Double)
            {
                writer.Write ((double) obj);
                return;
            }

            if (obj is Single)
            {
                writer.Write((float)obj);
                return;
            }

            if (obj is Int32)
            {
                writer.Write ((int) obj);
                return;
            }

            if (obj is Boolean)
            {
                writer.Write ((bool) obj);
                return;
            }

            if (obj is Int64)
            {
                writer.Write ((long) obj);
                return;
            }

            if (obj is Array)
            {
                writer.WriteArrayStart ();

                foreach (object elem in (Array) obj)
                    WriteValue (elem, writer, writer_is_private, depth + 1);

                writer.WriteArrayEnd ();

                return;
            }

            if (obj is IList)
            {
                writer.WriteArrayStart ();
                foreach (object elem in (IList) obj)
                    WriteValue (elem, writer, writer_is_private, depth + 1);
                writer.WriteArrayEnd ();

                return;
            }

            var iDictionary = obj as IDictionary;
            if (iDictionary != null)
            {
                writer.WriteObjectStart ();
                foreach (DictionaryEntry entity in iDictionary)
                {
                    var propertyName = entity.Key as string ?? Convert.ToString(entity.Key, CultureInfo.InvariantCulture);
                    writer.WritePropertyName (propertyName);
                    WriteValue (entity.Value, writer, writer_is_private,
                                depth + 1);
                }
                writer.WriteObjectEnd ();

                return;
            }

            Type obj_type = obj.GetType ();

            // See if there's a custom exporter for the object
            if (custom_exporters_table.TryGetValue(obj_type, out var customExporter))
            {
                customExporter(obj, writer);

                return;
            }

            // If not, maybe there's a base exporter
            if (base_exporters_table.TryGetValue(obj_type, out var baseExporter))
            {
                baseExporter(obj, writer);

                return;
            }

            // Last option, let's see if it's an enum
            if (obj is Enum)
            {
                Type e_type = Enum.GetUnderlyingType (obj_type);

                if (e_type == long_type
                    || e_type == typeof (uint)
                    || e_type == ulong_type)
                    writer.Write ((ulong) obj);
                else
                    writer.Write ((int) obj);

                return;
            }

            // Okay, so it looks like the input should be exported as an
            // object
            List<PropertyMetadata> props = AddTypeProperties(obj_type);

            writer.WriteObjectStart ();
            foreach (PropertyMetadata p_data in props)
            {
                if (p_data.IsField)
                {
                    writer.WritePropertyName (p_data.Info.Name);
                    WriteValue (((FieldInfo) p_data.Info).GetValue (obj),
                                writer, writer_is_private, depth + 1);
                }
                else
                {
                    PropertyInfo p_info = (PropertyInfo) p_data.Info;

                    if (p_info.CanRead)
                    {
                        writer.WritePropertyName (p_data.Info.Name);
                        WriteValue (p_info.GetValue (obj, null),
                                    writer, writer_is_private, depth + 1);
                    }
                }
            }
            writer.WriteObjectEnd ();
        }
        #endregion


        public static string ToJson (object obj)
        {
            static_writer ??= new JsonWriter();
                static_writer.Reset ();

                WriteValue (obj, static_writer, true, 0);

                return static_writer.ToString ();
            }

        public static void ToJson (object obj, JsonWriter writer)
        {
            WriteValue (obj, writer, false, 0);
        }

        public static JsonData ToObject (JsonReader reader)
        {
            return (JsonData) ToWrapper (
                delegate { return new JsonData (); }, reader);
        }

        public static object ToObject(Type toType, JsonReader reader)
        {
            return ReadValue(toType, reader);
        }

        public static JsonData ToObject (TextReader reader)
        {
            JsonReader json_reader = new JsonReader (reader);

            return (JsonData) ToWrapper (
                delegate { return new JsonData (); }, json_reader);
        }

        public static JsonData ToObject (string json)
        {
            return (JsonData) ToWrapper (
                delegate { return new JsonData (); }, json);
        }

        public static T ToObject<T> (JsonReader reader)
        {
            return (T) ReadValue (typeof (T), reader);
        }

        public static T ToObject<T> (TextReader reader)
        {
            JsonReader json_reader = new JsonReader (reader);

            return (T) ReadValue (typeof (T), json_reader);
        }

        public static T ToObject<T> (string json)
        {
            JsonReader reader = null;
            if (_readerPool.TryPop(out reader))
                reader.SetJson(json);
            else
                reader = new JsonReader(json, true);

            try
            {
            return (T) ReadValue (typeof (T), reader);
        }
            finally
            {
                _readerPool.Push(reader);
            }
        }

        public static object ToObject(Type toType, string json)
        {
            JsonReader reader = new JsonReader(json);

            return ReadValue(toType, reader);
        }

        public static IJsonWrapper ToWrapper (WrapperFactory factory,
                                              JsonReader reader)
        {
            return ReadValue (factory, reader);
        }

        public static IJsonWrapper ToWrapper (WrapperFactory factory,
                                              string json)
        {
            JsonReader reader = new JsonReader (json);

            return ReadValue (factory, reader);
        }

        public static void RegisterExporter<T> (ExporterFunc<T> exporter)
        {
            ExporterFunc exporter_wrapper =
                delegate (object obj, JsonWriter writer)
                {
                    exporter ((T) obj, writer);
                };

            custom_exporters_table[typeof (T)] = exporter_wrapper;
        }

        public static void RegisterImporter<TJson, TValue> (
            ImporterFunc<TJson, TValue> importer)
        {
            ImporterFunc importer_wrapper =
                delegate (object input)
                {
                    return importer ((TJson) input);
                };

            RegisterImporter (custom_importers_table, typeof (TJson),
                              typeof (TValue), importer_wrapper);
        }

        private static System.Func<Type, object> _instanceProvider;
        public static void RegisterInstanceProvider(System.Func<Type, object> provider)
        {
            _instanceProvider = provider;
        }

        public static void RegisterStringCache(string str)
        {
            _stringInstanceCache.TryAdd(str.AsMemory(), str);
        }

        public static string GetStringInstance(ReadOnlyMemory<char> chars)
        {
            if (_stringInstanceCache.TryGetValue(chars, out var value))
                return value;

            return new string(chars.Span);
        }

        public static object GetInstance(Type type)
        {
            var provider = _instanceProvider;
            object result = null;

            if (provider != null)
                result = provider(type);

            if (result == null)
                result = Activator.CreateInstance(type);

            return result;
        }

        public static void UnregisterExporters ()
        {
            custom_exporters_table.Clear ();
        }

        public static void UnregisterImporters ()
        {
            custom_importers_table.Clear ();
        }
    }
}
