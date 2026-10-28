using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Core.Grading;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>
    /// The grading layer's view of URP and Beautify, by reflection.
    /// <para>
    /// Why reflection: this adapter does not reference the URP assemblies at compile time (they are
    /// not in its project), and reflection gives the fail-soft behaviour grading needs for free:
    /// every type and member is resolved once, at
    /// <see cref="Bind"/>; anything missing (a URP update that renamed a field) turns grading off with
    /// one warning naming it instead of a <c>MissingFieldException</c> in a frame. The members are
    /// also in <see cref="GradingPatchTargets"/>, so seamcheck and the startup check name them.
    /// </para>
    /// <para>
    /// Types and members used, all verified against the installed
    /// <c>Unity.RenderPipelines.Core.Runtime.dll</c> / <c>Unity.RenderPipelines.Universal.Runtime.dll</c>
    /// (URP 14, Unity 2022.3.62f2) with a metadata-only load: <c>Volume</c> (priority, weight,
    /// sharedProfile, isGlobal), <c>VolumeProfile.Add(Type, bool)</c> and <c>components</c>,
    /// <c>VolumeComponent.SetAllOverridesTo(bool)</c>, <c>VolumeParameter.overrideState</c>,
    /// <c>VolumeParameter&lt;T&gt;.value</c>, <c>TextureCurve(Keyframe[], float, bool, in Vector2)</c> and
    /// <c>Release()</c>, <c>UniversalRenderPipeline.asset.colorGradingLutSize</c>, the URP components
    /// listed in <see cref="RequiredFields"/>, and <c>Beautify.Universal.Beautify.bloomIntensity</c>
    /// (Beautify is compiled into the URP runtime assembly in this game).
    /// </para>
    /// </summary>
    internal sealed class GradingUrp
    {
        internal const string CoreAssembly = GradingUrpMembers.CoreAssembly;
        internal const string UniversalAssembly = GradingUrpMembers.UniversalAssembly;
        internal const string UrpNamespace = GradingUrpMembers.UrpNamespace;
        internal const string BeautifyTypeName = GradingUrpMembers.BeautifyTypeName;
        internal const string BeautifyBloomField = GradingUrpMembers.BeautifyBloomField;

        /// <summary>Every URP component field the plan writes, by component.</summary>
        internal static IReadOnlyDictionary<string, string[]> RequiredFields => GradingUrpMembers.RequiredFields;

        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

        private readonly Dictionary<string, Type> _components = new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private readonly Dictionary<Type, PropertyInfo> _valueProperties = new Dictionary<Type, PropertyInfo>();

        private FieldInfo _priority = null!;
        private FieldInfo _weight = null!;
        private FieldInfo _sharedProfile = null!;
        private PropertyInfo _isGlobal = null!;
        private MethodInfo _profileAdd = null!;
        private FieldInfo _profileComponents = null!;
        private MethodInfo _setAllOverrides = null!;
        private PropertyInfo _overrideState = null!;
        private ConstructorInfo _curveCtor = null!;
        private MethodInfo? _curveRelease;
        private PropertyInfo? _pipelineAsset;
        private PropertyInfo? _lutSize;

        private GradingUrp()
        {
        }

        /// <summary>The <c>Volume</c> component type.</summary>
        internal Type VolumeType { get; private set; } = null!;

        /// <summary>The <c>VolumeProfile</c> type.</summary>
        internal Type ProfileType { get; private set; } = null!;

        /// <summary>Beautify's volume component, or null when this build has none.</summary>
        internal Type? BeautifyType { get; private set; }

        /// <summary>Why the bloom tweak is off, or null when Beautify's bloom is reachable.</summary>
        internal string? BeautifyProblem { get; private set; }

        /// <summary>
        /// Resolves everything. Returns null (and why) when a type or member the plan needs is missing.
        /// </summary>
        /// <param name="problem">Every missing piece, comma-separated, or null.</param>
        internal static GradingUrp? Bind(out string? problem)
        {
            var urp = new GradingUrp();
            var missing = new List<string>();

            var core = FindAssembly(CoreAssembly);
            var universal = FindAssembly(UniversalAssembly);
            if (core is null || universal is null)
            {
                problem = "assembly " + (core is null ? CoreAssembly : UniversalAssembly) + " is not loaded (not a URP game?)";
                return null;
            }

            urp.VolumeType = Need(core, "UnityEngine.Rendering.Volume", missing)!;
            urp.ProfileType = Need(core, "UnityEngine.Rendering.VolumeProfile", missing)!;
            var componentType = Need(core, "UnityEngine.Rendering.VolumeComponent", missing);
            var parameterType = Need(core, "UnityEngine.Rendering.VolumeParameter", missing);
            var curveType = Need(core, "UnityEngine.Rendering.TextureCurve", missing);
            var pipelineType = Need(universal, UrpNamespace + "UniversalRenderPipeline", missing);

            if (missing.Count > 0)
            {
                problem = "missing " + string.Join(", ", missing.ToArray());
                return null;
            }

            urp._priority = NeedField(urp.VolumeType, "priority", missing)!;
            urp._weight = NeedField(urp.VolumeType, "weight", missing)!;
            urp._sharedProfile = NeedField(urp.VolumeType, "sharedProfile", missing)!;
            urp._isGlobal = NeedProperty(urp.VolumeType, "isGlobal", missing)!;
            urp._profileAdd = NeedMethod(urp.ProfileType, "Add", new[] { typeof(Type), typeof(bool) }, missing)!;
            urp._profileComponents = NeedField(urp.ProfileType, "components", missing)!;
            urp._setAllOverrides = NeedMethod(componentType!, "SetAllOverridesTo", new[] { typeof(bool) }, missing)!;
            urp._overrideState = NeedProperty(parameterType!, "overrideState", missing)!;

            urp._curveCtor = curveType!.GetConstructor(
                new[] { typeof(Keyframe[]), typeof(float), typeof(bool), typeof(Vector2).MakeByRefType() })!;
            if (urp._curveCtor is null)
            {
                missing.Add("TextureCurve(Keyframe[], float, bool, in Vector2)");
            }

            urp._curveRelease = curveType.GetMethod("Release", Instance, null, Type.EmptyTypes, null);
            urp._pipelineAsset = pipelineType!.GetProperty("asset", BindingFlags.Public | BindingFlags.Static);
            urp._lutSize = urp._pipelineAsset?.PropertyType.GetProperty("colorGradingLutSize", Instance);

            foreach (var pair in RequiredFields)
            {
                var type = Need(universal, UrpNamespace + pair.Key, missing);
                if (type is null)
                {
                    continue;
                }

                urp._components[pair.Key] = type;
                foreach (var field in pair.Value)
                {
                    var info = NeedField(type, field, missing);
                    if (info != null)
                    {
                        urp._fields[pair.Key + "." + field] = info;
                    }
                }
            }

            if (missing.Count > 0)
            {
                problem = "missing " + string.Join(", ", missing.ToArray());
                return null;
            }

            // Beautify is optional: only the bloom tweak needs it.
            var beautify = universal.GetType(BeautifyTypeName, false) ?? FindTypeAnywhere(BeautifyTypeName);
            var bloom = beautify?.GetField(BeautifyBloomField, Instance);
            if (beautify is null || bloom is null)
            {
                urp.BeautifyProblem = beautify is null
                    ? BeautifyTypeName + " is not in this build"
                    : BeautifyTypeName + "." + BeautifyBloomField + " is missing";
            }
            else
            {
                urp.BeautifyType = beautify;
                urp._components["Beautify"] = beautify;
                urp._fields["Beautify." + BeautifyBloomField] = bloom;
            }

            problem = null;
            return urp;
        }

        /// <summary>The URP colour-grading LUT size (the LUT a ColorLookup accepts must match); 32 when unknown.</summary>
        internal int LutSize()
        {
            try
            {
                var asset = _pipelineAsset?.GetValue(null);
                if (asset != null && _lutSize?.GetValue(asset) is int size && size >= Lut3D.MinSize && size <= Lut3D.MaxSize)
                {
                    return size;
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("GRADING: could not read the URP LUT size: " + ex.GetType().Name + ": " + ex.Message);
            }

            return 32;
        }

        /// <summary>Adds a <c>Volume</c> to a GameObject: global, at the given priority, weight 1.</summary>
        internal Component AddVolume(GameObject host, float priority)
        {
            var volume = host.AddComponent(VolumeType);
            _isGlobal.SetValue(volume, true);
            _priority.SetValue(volume, priority);
            _weight.SetValue(volume, 1f);
            return volume;
        }

        /// <summary>Sets the volume's weight (0 = the game's own look, 1 = graded).</summary>
        internal void SetWeight(Component volume, float weight) => _weight.SetValue(volume, weight);

        /// <summary>The volume's weight.</summary>
        internal float GetWeight(Component volume) => (float)_weight.GetValue(volume);

        /// <summary>The volume's priority.</summary>
        internal float GetPriority(Component volume) => (float)_priority.GetValue(volume);

        /// <summary>The volume's profile.</summary>
        internal ScriptableObject? GetProfile(Component volume) => _sharedProfile.GetValue(volume) as ScriptableObject;

        /// <summary>Points the volume at another profile.</summary>
        internal void SetProfile(Component volume, ScriptableObject? profile) => _sharedProfile.SetValue(volume, profile);

        /// <summary>Creates an empty profile (in memory; never saved).</summary>
        internal ScriptableObject CreateProfile()
        {
            var profile = ScriptableObject.CreateInstance(ProfileType);
            profile.name = "CWY.Grading.Profile";
            profile.hideFlags = HideFlags.DontSave;
            return profile;
        }

        /// <summary>The components of a profile (for teardown).</summary>
        internal IList? ComponentsOf(ScriptableObject profile) => _profileComponents.GetValue(profile) as IList;

        /// <summary>
        /// Adds one component to a profile with every parameter's override off - so it changes only
        /// the parameters <see cref="Set"/> turns on (Beautify, for one, constructs many of its
        /// parameters with override on).
        /// </summary>
        /// <param name="profile">The profile.</param>
        /// <param name="component">Key of <see cref="RequiredFields"/>, or <c>Beautify</c>.</param>
        internal ScriptableObject AddComponent(ScriptableObject profile, string component)
        {
            var type = _components[component];
            var added = (ScriptableObject)_profileAdd.Invoke(profile, new object[] { type, false });
            _setAllOverrides.Invoke(added, new object[] { false });
            added.hideFlags = HideFlags.DontSave;
            return added;
        }

        /// <summary>Turns one parameter's override on and sets its value.</summary>
        /// <param name="component">A component from <see cref="AddComponent"/>.</param>
        /// <param name="key">Component and field, e.g. <c>ColorAdjustments.postExposure</c>.</param>
        /// <param name="value">The value, already of the parameter's type (float, Color, Vector4, bool, Texture, TextureCurve, enum).</param>
        internal void Set(ScriptableObject component, string key, object? value)
        {
            var parameter = _fields[key].GetValue(component);
            if (parameter is null)
            {
                throw new InvalidOperationException(key + " has no parameter object");
            }

            ValueProperty(parameter.GetType()).SetValue(parameter, value);
            _overrideState.SetValue(parameter, true);
        }

        /// <summary>A parameter's value type (e.g. FilmGrainLookup for <c>FilmGrain.type</c>).</summary>
        internal Type ValueTypeOf(ScriptableObject component, string key) =>
            ValueProperty(_fields[key].GetValue(component)!.GetType()).PropertyType;

        /// <summary>Creates a URP <c>TextureCurve</c> from keys.</summary>
        /// <param name="keys">Keys sorted by time.</param>
        /// <param name="zeroValue">Value of an empty curve (0 for YRGB, 0.5 for the HSV curves).</param>
        /// <param name="loop">True for hue curves.</param>
        internal object CreateCurve(IReadOnlyList<CurveKey> keys, float zeroValue, bool loop)
        {
            var frames = new Keyframe[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                frames[i] = new Keyframe(
                    (float)keys[i].Time, (float)keys[i].Value, (float)keys[i].InTangent, (float)keys[i].OutTangent);
            }

            return _curveCtor.Invoke(new object[] { frames, zeroValue, loop, new Vector2(0f, 1f) });
        }

        /// <summary>Frees a curve's baked texture.</summary>
        internal void ReleaseCurve(object curve)
        {
            try
            {
                _curveRelease?.Invoke(curve, null);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("GRADING: TextureCurve.Release failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The game's own Beautify bloom right now: the active global volumes (ours excluded) blended
        /// in priority order the way URP's VolumeManager blends them. False when nothing sets it.
        /// </summary>
        /// <param name="ours">Our own volume, skipped.</param>
        /// <param name="value">The blended intensity.</param>
        internal bool TryReadGameBloom(Component? ours, out float value)
        {
            value = 0f;
            if (BeautifyType is null)
            {
                return false;
            }

            var found = new List<(float Priority, float Weight, float Value)>();
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(VolumeType))
            {
                if (!(obj is Behaviour volume) || volume == ours || !volume.isActiveAndEnabled)
                {
                    continue;
                }

                var weight = Mathf.Clamp01((float)_weight.GetValue(volume));
                if (weight <= 0f || !(bool)_isGlobal.GetValue(volume))
                {
                    continue;
                }

                if (!(_sharedProfile.GetValue(volume) is ScriptableObject profile) || !(ComponentsOf(profile) is IList components))
                {
                    continue;
                }

                foreach (var component in components)
                {
                    if (component is null || !BeautifyType.IsInstanceOfType(component))
                    {
                        continue;
                    }

                    var parameter = _fields["Beautify." + BeautifyBloomField].GetValue(component);
                    if (parameter != null && (bool)_overrideState.GetValue(parameter) &&
                        ValueProperty(parameter.GetType()).GetValue(parameter) is float bloom)
                    {
                        found.Add(((float)_priority.GetValue(volume), weight, bloom));
                    }
                }
            }

            if (found.Count == 0)
            {
                return false;
            }

            found.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            foreach (var item in found)
            {
                value = Mathf.Lerp(value, item.Value, item.Weight);
            }

            return true;
        }

        private PropertyInfo ValueProperty(Type parameterType)
        {
            if (_valueProperties.TryGetValue(parameterType, out var cached))
            {
                return cached;
            }

            // Walk from the most derived type: ClampedFloatParameter overrides 'value' (it clamps), and
            // a plain GetProperty could see both declarations.
            for (var type = parameterType; type != null; type = type.BaseType)
            {
                var property = type.GetProperty("value", Instance | BindingFlags.DeclaredOnly);
                if (property != null && property.CanWrite)
                {
                    _valueProperties[parameterType] = property;
                    return property;
                }
            }

            throw new MissingMemberException(parameterType.FullName, "value");
        }

        private static Assembly? FindAssembly(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }

            return null;
        }

        private static Type? FindTypeAnywhere(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static Type? Need(Assembly assembly, string fullName, List<string> missing)
        {
            var type = assembly.GetType(fullName, false);
            if (type is null)
            {
                missing.Add(fullName);
            }

            return type;
        }

        private static FieldInfo? NeedField(Type type, string name, List<string> missing)
        {
            var field = type.GetField(name, Instance);
            if (field is null)
            {
                missing.Add(type.Name + "." + name);
            }

            return field;
        }

        private static PropertyInfo? NeedProperty(Type type, string name, List<string> missing)
        {
            var property = type.GetProperty(name, Instance);
            if (property is null || !property.CanWrite)
            {
                missing.Add(type.Name + "." + name);
            }

            return property;
        }

        private static MethodInfo? NeedMethod(Type type, string name, Type[] parameters, List<string> missing)
        {
            var method = type.GetMethod(name, Instance, null, parameters, null);
            if (method is null)
            {
                missing.Add(type.Name + "." + name + "(" + parameters.Length + " args)");
            }

            return method;
        }
    }
}
