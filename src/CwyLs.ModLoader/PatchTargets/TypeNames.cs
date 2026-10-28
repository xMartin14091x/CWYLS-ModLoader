using System;
using System.Reflection;
using System.Text;

namespace CwyLs.ModLoader
{
    /// <summary>
    /// Renders a <see cref="Type"/> as a readable full name. <see cref="Type.FullName"/> is
    /// unusable for a closed generic - it spells every argument out assembly-qualified inside
    /// double brackets - so generics are rebuilt here in source form before
    /// <see cref="Core.PatchSignature.ShortName"/> strips the namespaces.
    /// </summary>
    public static class TypeNames
    {
        /// <summary>
        /// Full name of a type in C#-like form, e.g.
        /// <c>System.Collections.Generic.Dictionary&lt;System.String, UnityEngine.AudioClip&gt;</c>.
        /// A nested type keeps the <c>+</c> separator that reflection uses, so it round-trips
        /// against a <see cref="Core.PatchTarget.ParameterTypeNames"/> entry either way.
        /// </summary>
        /// <param name="type">The type to render; null renders as <c>?</c>.</param>
        public static string Readable(Type? type)
        {
            if (type == null)
            {
                return Core.PatchSignature.UnknownType;
            }

            if (type.IsByRef)
            {
                return Readable(type.GetElementType()) + "&";
            }

            if (type.IsArray)
            {
                return Readable(type.GetElementType()) + "[]";
            }

            if (type.IsPointer)
            {
                return Readable(type.GetElementType()) + "*";
            }

            if (type.IsGenericParameter)
            {
                return type.Name;
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var name = definition.FullName ?? definition.Name;
                var tick = name.IndexOf('`');
                if (tick >= 0)
                {
                    name = name.Substring(0, tick);
                }

                var sb = new StringBuilder(name).Append('<');
                var arguments = type.GetGenericArguments();
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(Readable(arguments[i]));
                }

                return sb.Append('>').ToString();
            }

            return type.FullName ?? type.Name;
        }

        /// <summary>Canonical signature of a method as the runtime actually declares it.</summary>
        /// <param name="method">The resolved method.</param>
        /// <exception cref="ArgumentNullException"><paramref name="method"/> is null.</exception>
        public static string Signature(MethodInfo method)
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            var parameters = method.GetParameters();
            var names = new string[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                names[i] = Readable(parameters[i].ParameterType);
            }

            return Core.PatchSignature.ForMethod(Readable(method.ReturnType), method.Name, names, method.IsStatic);
        }

        /// <summary>Canonical signature of a field as the runtime actually declares it.</summary>
        /// <param name="field">The resolved field.</param>
        /// <exception cref="ArgumentNullException"><paramref name="field"/> is null.</exception>
        public static string Signature(FieldInfo field)
        {
            if (field == null)
            {
                throw new ArgumentNullException(nameof(field));
            }

            return Core.PatchSignature.ForField(Readable(field.FieldType), field.Name, field.IsStatic);
        }

        /// <summary>Canonical signature of a property as the runtime actually declares it.</summary>
        /// <param name="property">The resolved property.</param>
        /// <exception cref="ArgumentNullException"><paramref name="property"/> is null.</exception>
        public static string Signature(PropertyInfo property)
        {
            if (property == null)
            {
                throw new ArgumentNullException(nameof(property));
            }

            var accessor = property.GetGetMethod(true) ?? property.GetSetMethod(true);
            return Core.PatchSignature.ForProperty(
                Readable(property.PropertyType),
                property.Name,
                property.CanRead,
                property.CanWrite,
                accessor != null && accessor.IsStatic);
        }
    }
}
