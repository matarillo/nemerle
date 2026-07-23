// Nemerle.CoreEmit.AttributeBlob: CoreCLR-only workaround for a persisted-SRE
// (System.Reflection.Emit.PersistedAssemblyBuilder) gap.
//
// TypeBuilderImpl.UnderlyingSystemType returns `this` instead of the enum's underlying
// type, unlike both .NET Framework's TypeBuilder and CoreCLR's runtime RuntimeTypeBuilder,
// which special-case unbaked enum TypeBuilders. That makes Type.GetTypeCode return
// TypeCode.Object for any not-yet-created (still being compiled) local enum type, so the
// public System.Reflection.Emit.CustomAttributeBuilder constructor's internal
// VerifyTypeAndPassedObjectType check always throws
// ArgumentException("Constant does not match the defined type.") whenever an attribute
// argument or named member's declared type is (or is a 1-D array of) an enum defined in
// the same compilation ("local enum"). See dotnet-port\docs\15-attributes01-diagnosis.md for
// the full root-cause analysis and dotnet-port\docs\17-resources-fixes-log.md for the fix log.
//
// This class hand-assembles the ECMA-335 SS II.23.3 custom-attribute blob -- a faithful
// port of corelib's CustomAttributeBuilder private EmitType/EmitValue/EmitString -- and
// injects it into a CustomAttributeBuilder instance created via
// RuntimeHelpers.GetUninitializedObject + private-field assignment, bypassing the
// constructor's validation entirely. The only behavioral differences from corelib's own
// encoder are (1) no type-code cross-check against the broken UnderlyingSystemType, and
// (2) a local (TypeBuilder) enum's type name is written as `FullName + ", " +
// Assembly.FullName` instead of `AssemblyQualifiedName` (which throws
// NotSupportedException on TypeBuilderImpl for any not-yet-created type). The resulting
// object is otherwise indistinguishable from a normally-constructed
// CustomAttributeBuilder to every SetCustomAttribute overload (they only ever read the
// private Ctor/Data properties via the internal Ctor/Data accessors).
//
// Used only when at least one of the constructor parameter types / named property types /
// named field types involves a local enum (NeedsWorkaround) -- everything else keeps using
// the normal, fully-validated public CustomAttributeBuilder constructor.

using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Text = System.Text;

namespace Nemerle.CoreEmit
{
    public static class AttributeBlob
    {
        // ECMA-335 II.23.3 element-type tags used inside a custom-attribute blob
        // (values match corelib's internal System.Reflection.Emit.CustomAttributeEncoding).
        private enum Encoding : byte
        {
            Boolean = 0x02,
            Char    = 0x03,
            SByte   = 0x04,
            Byte    = 0x05,
            Int16   = 0x06,
            UInt16  = 0x07,
            Int32   = 0x08,
            UInt32  = 0x09,
            Int64   = 0x0a,
            UInt64  = 0x0b,
            Float   = 0x0c,
            Double  = 0x0d,
            String  = 0x0e,
            Array   = 0x1d,
            Type    = 0x50,
            Object  = 0x51,
            Field   = 0x53,
            Property = 0x54,
            Enum    = 0x55,
        }

        private static bool IsLocalEnum(Type t) =>
            (t.IsEnum && t is TypeBuilder) || (t.IsArray && IsLocalEnum(t.GetElementType()));

        /// <summary>True when the normal CustomAttributeBuilder constructor cannot be used
        /// because a local (still-being-compiled) enum type appears among the constructor
        /// parameter types / named property types / named field types.</summary>
        public static bool NeedsWorkaround(ConstructorInfo con, PropertyInfo[] props, FieldInfo[] fields)
        {
            foreach (ParameterInfo p in con.GetParameters())
                if (IsLocalEnum(p.ParameterType))
                    return true;
            foreach (PropertyInfo p in props)
                if (IsLocalEnum(p.PropertyType))
                    return true;
            foreach (FieldInfo f in fields)
                if (IsLocalEnum(f.FieldType))
                    return true;
            return false;
        }

        public static CustomAttributeBuilder Create(
            ConstructorInfo con, object[] ctorArgs,
            PropertyInfo[] props, object[] propVals,
            FieldInfo[] fields, object[] fieldVals)
        {
            if (!NeedsWorkaround(con, props, fields))
                return new CustomAttributeBuilder(con, ctorArgs, props, propVals, fields, fieldVals);

            byte[] blob = EncodeBlob(con, ctorArgs, props, propVals, fields, fieldVals);

            object cab = RuntimeHelpers.GetUninitializedObject(typeof(CustomAttributeBuilder));
            SetPrivate(cab, "m_con", con);
            SetPrivate(cab, "m_constructorArgs", (object[])ctorArgs.Clone());
            SetPrivate(cab, "m_blob", blob);
            return (CustomAttributeBuilder)cab;
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo fi = typeof(CustomAttributeBuilder).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null)
                throw new InvalidOperationException(
                    "Nemerle.CoreEmit.AttributeBlob: System.Reflection.Emit.CustomAttributeBuilder no longer has " +
                    "a private field named '" + fieldName + "' on this runtime -- the persisted-local-enum-attribute " +
                    "workaround (see dotnet-port\\15-attributes01-diagnosis.md) needs updating.");
            fi.SetValue(target, value);
        }

        private static byte[] EncodeBlob(
            ConstructorInfo con, object[] ctorArgs,
            PropertyInfo[] props, object[] propVals,
            FieldInfo[] fields, object[] fieldVals)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)1); // blob prolog / version

                ParameterInfo[] pars = con.GetParameters();
                for (int i = 0; i < pars.Length; i++)
                    EmitValue(writer, pars[i].ParameterType, ctorArgs[i]);

                writer.Write((ushort)(props.Length + fields.Length));

                for (int i = 0; i < props.Length; i++)
                {
                    writer.Write((byte)Encoding.Property);
                    EmitType(writer, props[i].PropertyType);
                    EmitString(writer, props[i].Name);
                    EmitValue(writer, props[i].PropertyType, propVals[i]);
                }

                for (int i = 0; i < fields.Length; i++)
                {
                    writer.Write((byte)Encoding.Field);
                    EmitType(writer, fields[i].FieldType);
                    EmitString(writer, fields[i].Name);
                    EmitValue(writer, fields[i].FieldType, fieldVals[i]);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        // AssemblyQualifiedName throws NotSupportedException on an unbaked TypeBuilderImpl;
        // FullName + ", " + Assembly.FullName is what CreateType() would eventually resolve
        // to and is exactly what the metadata reader needs to locate the (same-assembly)
        // enum type once the attribute consumer loads the finished assembly.
        private static string TypeName(Type t) =>
            t is TypeBuilder ? t.FullName + ", " + t.Assembly.FullName : t.AssemblyQualifiedName;

        // Enum.GetUnderlyingType(t) ultimately delegates to t.GetEnumUnderlyingType(); called
        // directly here since that's the documented-safe entry point for an unbaked
        // TypeBuilder enum (works once the enum's instance "value__" field has been defined,
        // which Nemerle always does before attributes referencing the enum are compiled).
        private static Type EnumUnderlyingType(Type t) =>
            t is TypeBuilder tb ? tb.GetEnumUnderlyingType() : Enum.GetUnderlyingType(t);

        private static void EmitType(BinaryWriter writer, Type type)
        {
            if (type.IsPrimitive)
            {
                writer.Write((byte)PrimitiveEncoding(type));
            }
            else if (type.IsEnum)
            {
                writer.Write((byte)Encoding.Enum);
                EmitString(writer, TypeName(type));
            }
            else if (type == typeof(string))
            {
                writer.Write((byte)Encoding.String);
            }
            else if (type == typeof(Type))
            {
                writer.Write((byte)Encoding.Type);
            }
            else if (type.IsArray)
            {
                writer.Write((byte)Encoding.Array);
                EmitType(writer, type.GetElementType());
            }
            else
            {
                writer.Write((byte)Encoding.Object);
            }
        }

        private static Encoding PrimitiveEncoding(Type type)
        {
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.SByte:   return Encoding.SByte;
                case TypeCode.Byte:    return Encoding.Byte;
                case TypeCode.Char:    return Encoding.Char;
                case TypeCode.Boolean: return Encoding.Boolean;
                case TypeCode.Int16:   return Encoding.Int16;
                case TypeCode.UInt16:  return Encoding.UInt16;
                case TypeCode.Int32:   return Encoding.Int32;
                case TypeCode.UInt32:  return Encoding.UInt32;
                case TypeCode.Int64:   return Encoding.Int64;
                case TypeCode.UInt64:  return Encoding.UInt64;
                case TypeCode.Single:  return Encoding.Float;
                case TypeCode.Double:  return Encoding.Double;
                default:
                    throw new ArgumentException("invalid primitive type in custom attribute: " + type);
            }
        }

        // ECMA-335 compressed unsigned integer (metadata "PackedLen"): 1/2/4-byte length
        // prefix followed by the raw UTF-8 bytes.
        private static void EmitString(BinaryWriter writer, string str)
        {
            byte[] utf8 = Text.Encoding.UTF8.GetBytes(str);
            uint length = (uint)utf8.Length;
            if (length <= 0x7f)
            {
                writer.Write((byte)length);
            }
            else if (length <= 0x3fff)
            {
                writer.Write((byte)((length >> 8) | 0x80));
                writer.Write((byte)(length & 0xff));
            }
            else
            {
                writer.Write((byte)((length >> 24) | 0xc0));
                writer.Write((byte)((length >> 16) & 0xff));
                writer.Write((byte)((length >> 8) & 0xff));
                writer.Write((byte)(length & 0xff));
            }
            writer.Write(utf8);
        }

        private static void EmitValue(BinaryWriter writer, Type type, object value)
        {
            if (type.IsEnum)
            {
                EmitPrimitiveValue(writer, EnumUnderlyingType(type), value);
            }
            else if (type == typeof(string))
            {
                if (value == null)
                    writer.Write((byte)0xff);
                else
                    EmitString(writer, (string)value);
            }
            else if (type == typeof(Type))
            {
                if (value == null)
                    writer.Write((byte)0xff);
                else
                    EmitString(writer, TypeName((Type)value));
            }
            else if (type.IsArray)
            {
                if (value == null)
                {
                    writer.Write((uint)0xffffffff);
                }
                else
                {
                    Array a = (Array)value;
                    Type et = type.GetElementType();
                    writer.Write(a.Length);
                    for (int i = 0; i < a.Length; i++)
                        EmitValue(writer, et, a.GetValue(i));
                }
            }
            else if (type.IsPrimitive)
            {
                EmitPrimitiveValue(writer, type, value);
            }
            else if (type == typeof(object))
            {
                // Tagged object case: canonicalize the runtime value's own type (Type
                // instances are never actually System.Type, they're RuntimeType/TypeBuilder/...).
                Type ot = value == null ? typeof(string) : (value is Type ? typeof(Type) : value.GetType());
                if (ot == typeof(object))
                    throw new ArgumentException("System.Object is not a valid custom-attribute typed-argument type");
                EmitType(writer, ot);
                EmitValue(writer, ot, value);
            }
            else
            {
                throw new ArgumentException("invalid custom-attribute argument type: " +
                    (value == null ? "null" : value.GetType().ToString()));
            }
        }

        // Uses Convert.ToXxx rather than a direct unboxing cast: tolerant of a boxed actual
        // enum instance (imported enums, via System.Enum.ToObject) as well as a boxed
        // underlying integral value (local/TypeBuilder enums, since Enum.ToObject cannot
        // target a TypeBuilder -- see ncc\parsing\AST.n Literal.AsObject's Literal.Enum
        // case) -- both are valid CLR unboxing targets for GetTypeCode-equal types, and
        // Convert additionally handles IConvertible without requiring an exact box match.
        private static void EmitPrimitiveValue(BinaryWriter writer, Type type, object value)
        {
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.SByte:   writer.Write(Convert.ToSByte(value)); break;
                case TypeCode.Byte:    writer.Write(Convert.ToByte(value)); break;
                case TypeCode.Char:    writer.Write(Convert.ToUInt16(Convert.ToChar(value))); break;
                case TypeCode.Boolean: writer.Write((byte)(Convert.ToBoolean(value) ? 1 : 0)); break;
                case TypeCode.Int16:   writer.Write(Convert.ToInt16(value)); break;
                case TypeCode.UInt16:  writer.Write(Convert.ToUInt16(value)); break;
                case TypeCode.Int32:   writer.Write(Convert.ToInt32(value)); break;
                case TypeCode.UInt32:  writer.Write(Convert.ToUInt32(value)); break;
                case TypeCode.Int64:   writer.Write(Convert.ToInt64(value)); break;
                case TypeCode.UInt64:  writer.Write(Convert.ToUInt64(value)); break;
                case TypeCode.Single:  writer.Write(Convert.ToSingle(value)); break;
                case TypeCode.Double:  writer.Write(Convert.ToDouble(value)); break;
                default:
                    throw new ArgumentException("invalid primitive type in custom attribute: " + type);
            }
        }
    }
}
