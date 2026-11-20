using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace DatvTx.Core
{
    /// <summary>Shows enum [Description] text in the PropertyGrid and combo boxes.</summary>
    public class EnumDescConverter : EnumConverter
    {
        private readonly Type _type;

        public EnumDescConverter(Type type) : base(type) { _type = type; }

        public static string Describe(object value)
        {
            if (value == null) return "";
            FieldInfo fi = value.GetType().GetField(value.ToString());
            if (fi == null) return value.ToString();
            var attr = (DescriptionAttribute)Attribute.GetCustomAttribute(fi, typeof(DescriptionAttribute));
            return attr != null ? attr.Description : value.ToString();
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value != null && value.GetType() == _type)
                return Describe(value);
            return base.ConvertTo(context, culture, value, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            var text = value as string;
            if (text != null)
            {
                foreach (FieldInfo fi in _type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var attr = (DescriptionAttribute)Attribute.GetCustomAttribute(fi, typeof(DescriptionAttribute));
                    if ((attr != null && attr.Description == text) || fi.Name == text)
                        return Enum.Parse(_type, fi.Name);
                }
            }
            return base.ConvertFrom(context, culture, value);
        }
    }
}
