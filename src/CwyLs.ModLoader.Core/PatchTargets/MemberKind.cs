namespace CwyLs.ModLoader.Core
{
    /// <summary>The kind of member a <see cref="PatchTarget"/> points at.</summary>
    public enum MemberKind
    {
        /// <summary>A method, resolved by name and exact parameter type list.</summary>
        Method,

        /// <summary>A field, resolved by name.</summary>
        Field,

        /// <summary>A property, resolved by name.</summary>
        Property
    }
}
