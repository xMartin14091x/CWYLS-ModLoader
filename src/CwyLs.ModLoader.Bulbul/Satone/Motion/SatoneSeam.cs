using System;
using System.Globalization;
using System.Reflection;
using CwyLs.ModLoader.Bulbul.Events;
using HarmonyLib;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// The thin layer between <see cref="SatoneMotionFeature"/> and the game: resolves
    /// <c>Bulbul.HeroineService</c> out of the room scope, and reaches the two seams that cannot be
    /// called directly from this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why reflection for the look seam.</b>
    /// <c>HeroineService.ChangeLookScaleAnimation(float, float, Ease = Ease.Unset)</c>
    /// has a DOTween <c>DG.Tweening.Ease</c> parameter. Calling it from C#
    /// - even leaving the optional argument out - requires the compiler to materialise that
    /// default, so it would force a DOTween reference into this adapter for one argument that is
    /// always <c>Ease.Unset</c>. The method is looked up once and the default is built from the
    /// parameter's own type, so a DOTween version change cannot desynchronise it.
    /// </para>
    /// <para>
    /// <b>Why reflection for the Animator.</b> Read-back of the <c>MotionType</c>, <c>Facial</c>
    /// and <c>LookScale</c> parameters is what turns "we called the method" into "the game holds
    /// the value", and it is the only evidence the facial path can produce at all - nothing on
    /// <c>HeroineService</c> exposes the current expression. <c>UnityEngine.Animator</c> lives in
    /// <c>UnityEngine.AnimationModule</c>, which this adapter does not reference, so the type is
    /// resolved by name at runtime. Read-back is a diagnostic: when it is unavailable the API
    /// still works and simply reports less.
    /// </para>
    /// </remarks>
    internal static class SatoneSeam
    {
        internal const string AnimatorTypeName = "UnityEngine.Animator, UnityEngine.AnimationModule";

        /// <summary>Animator parameter holding the body motion (set by <c>HeroineService</c>).</summary>
        internal const string MotionParameter = "MotionType";

        /// <summary>Animator parameter holding the expression (set by <c>HeroineFacialController</c>).</summary>
        internal const string FacialParameter = "Facial";

        /// <summary>Animator parameter holding the look weight (set by <c>HeroineLookDirectionService</c>).</summary>
        internal const string LookParameter = "LookScale";

        /// <summary>
        /// The game's own "hold the expression this long after a talk, then reset it" delay -
        /// <c>Bulbul.MyDefine.InitFacialDelaySecondsAfterTalk</c>. Used when the
        /// constant cannot be read back from the running game.
        /// </summary>
        internal const float DefaultFacialResetDelaySeconds = 0.77f;

        /// <summary>Name of the constant <see cref="FacialResetDelaySeconds"/> reads.</summary>
        internal const string FacialDelayConstant = "InitFacialDelaySecondsAfterTalk";

        /// <summary>The private HeroineAI field on HeroineService.</summary>
        private const string HeroineAiField = "_heroineAI";

        private static readonly object Gate = new object();

        private static MethodInfo? _changeLookScale;
        private static object? _easeUnset;
        private static string _lookSeamDetail = "not resolved yet";

        private static Type? _animatorType;
        private static MethodInfo? _getInteger;
        private static MethodInfo? _getFloat;
        private static MethodInfo? _getComponent;
        private static string _animatorSeamDetail = "not resolved yet";

        private static float? _facialResetDelaySeconds;
        private static string _facialDelayDetail = "not resolved yet";

        private static FieldInfo? _heroineAi;
        private static bool _heroineAiResolved;
        private static string _heroineAiDetail = "not resolved yet";

        /// <summary>What happened when the look seam was last resolved; for the smoke report.</summary>
        internal static string LookSeamDetail
        {
            get { lock (Gate) { return _lookSeamDetail; } }
        }

        /// <summary>What happened when the Animator read-back was last resolved.</summary>
        internal static string AnimatorSeamDetail
        {
            get { lock (Gate) { return _animatorSeamDetail; } }
        }

        /// <summary>How the facial delay was obtained, for the smoke report.</summary>
        internal static string FacialDelayDetail
        {
            get { lock (Gate) { return _facialDelayDetail; } }
        }

        /// <summary>How the HeroineAI field was resolved, for the smoke report.</summary>
        internal static string ActionChangeableDetail
        {
            get { lock (Gate) { return _heroineAiDetail; } }
        }

        /// <summary>
        /// The game's own post-talk expression delay, read from
        /// <c>Bulbul.MyDefine.InitFacialDelaySecondsAfterTalk</c> at runtime.
        /// </summary>
        /// <remarks>
        /// It is a <c>const</c>, so referencing it from C# would bake 0.77 into our DLL at compile
        /// time and a game update that retunes it would silently desynchronise us. Reading the
        /// literal out of the loaded assembly keeps us on the game's number, and falling back to
        /// <see cref="DefaultFacialResetDelaySeconds"/> keeps the restore working if the constant
        /// is ever renamed.
        /// </remarks>
        internal static float FacialResetDelaySeconds
        {
            get
            {
                lock (Gate)
                {
                    if (_facialResetDelaySeconds.HasValue)
                    {
                        return _facialResetDelaySeconds.Value;
                    }

                    _facialResetDelaySeconds = ResolveFacialDelay();
                    return _facialResetDelaySeconds.Value;
                }
            }
        }

        /// <summary>
        /// Whether the AI is between action changes, so re-dispatching her state is safe. This is
        /// the same question the game asks itself before it re-dispatches after a click reaction
        /// (whether HeroineAI's update state is not <c>Changing</c>, which
        /// <c>HeroineAI.IsPossibleChangeAction</c> answers).
        /// </summary>
        /// <param name="heroine">The live service.</param>
        /// <param name="changeable">
        /// The game's answer, or <c>true</c> when it could not be asked - an unavailable diagnostic
        /// must not block the restore that fixes a stuck pose.
        /// </param>
        /// <param name="detail">How the answer was obtained, or why it could not be.</param>
        /// <returns>True when the game actually answered.</returns>
        internal static bool TryIsActionChangeable(
            Game.HeroineService heroine, out bool changeable, out string detail)
        {
            FieldInfo? field;
            lock (Gate)
            {
                if (!_heroineAiResolved)
                {
                    _heroineAiResolved = true;
                    _heroineAi = AccessTools.Field(typeof(Game.HeroineService), HeroineAiField);
                    _heroineAiDetail = _heroineAi is null
                        ? "HeroineService." + HeroineAiField + " was not found; the AI cannot be asked "
                          + "whether it is mid-change"
                        : "HeroineService." + HeroineAiField + " bound";
                }

                field = _heroineAi;
                detail = _heroineAiDetail;
            }

            changeable = true;
            if (field is null)
            {
                return false;
            }

            try
            {
                if (field.GetValue(heroine) is not global::HeroineAI ai)
                {
                    detail = HeroineAiField + " is null on this HeroineService";
                    return false;
                }

                changeable = ai.IsPossibleChangeAction();
                detail = "HeroineAI.IsPossibleChangeAction()=" + (changeable ? "true" : "false");
                return true;
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                detail = "HeroineAI.IsPossibleChangeAction could not be read: "
                         + inner.GetType().Name + ": " + inner.Message;
                CwyLsModLoader.Log.LogDebug("SATONE " + detail);
                return false;
            }
        }

        /// <summary>
        /// Resolves the live <c>HeroineService</c>, or false while RoomScene is not loaded.
        /// </summary>
        /// <param name="heroine">The service, or null.</param>
        internal static bool TryHeroine(out Game.HeroineService? heroine) =>
            GameScopes.TryRoom(out heroine);

        /// <summary>True when RoomScene is loaded, so the room scope may be asked at all.</summary>
        internal static bool IsRoomLoaded => GameScopes.IsRoomLoaded;

        /// <summary>
        /// Calls <c>HeroineService.ChangeLookScaleAnimation(scale, seconds, Ease.Unset)</c>.
        /// </summary>
        /// <param name="heroine">The live service.</param>
        /// <param name="scale">Look weight, 0 = look away, 1 = look at the player.</param>
        /// <param name="seconds">Tween duration.</param>
        /// <param name="error">Why it failed, when it did.</param>
        /// <returns>True when the game's method ran.</returns>
        internal static bool TryChangeLookScale(
            Game.HeroineService heroine, float scale, float seconds, out string error)
        {
            MethodInfo? method;
            object? ease;
            lock (Gate)
            {
                if (_changeLookScale is null)
                {
                    ResolveLookSeam();
                }

                method = _changeLookScale;
                ease = _easeUnset;
            }

            if (method is null || ease is null)
            {
                error = LookSeamDetail;
                return false;
            }

            try
            {
                method.Invoke(heroine, new object[] { scale, seconds, ease });
                error = string.Empty;
                return true;
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                error = "ChangeLookScaleAnimation threw " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "ChangeLookScaleAnimation could not be invoked: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads an integer Animator parameter off Satone's Animator, or null when the read-back
        /// is unavailable (see the type note on this class).
        /// </summary>
        /// <param name="heroine">The live service; its GameObject carries the Animator.</param>
        /// <param name="parameter">Parameter name, e.g. <see cref="MotionParameter"/>.</param>
        internal static int? ReadInteger(Game.HeroineService heroine, string parameter)
        {
            var value = Read(heroine, parameter, integer: true);
            return value is int number ? number : (int?)null;
        }

        /// <summary>Reads a float Animator parameter, or null when the read-back is unavailable.</summary>
        /// <param name="heroine">The live service.</param>
        /// <param name="parameter">Parameter name, e.g. <see cref="LookParameter"/>.</param>
        internal static float? ReadFloat(Game.HeroineService heroine, string parameter)
        {
            var value = Read(heroine, parameter, integer: false);
            return value is float number ? number : (float?)null;
        }

        private static object? Read(Game.HeroineService heroine, string parameter, bool integer)
        {
            MethodInfo? getter;
            MethodInfo? getComponent;
            Type? animatorType;
            lock (Gate)
            {
                if (_animatorType is null)
                {
                    ResolveAnimatorSeam();
                }

                animatorType = _animatorType;
                getComponent = _getComponent;
                getter = integer ? _getInteger : _getFloat;
            }

            if (animatorType is null || getComponent is null || getter is null)
            {
                return null;
            }

            try
            {
                var animator = getComponent.Invoke(heroine, new object[] { animatorType });
                return animator is null ? null : getter.Invoke(animator, new object[] { parameter });
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                lock (Gate)
                {
                    _animatorSeamDetail = "read-back failed: " + inner.GetType().Name + ": " + inner.Message;
                }

                CwyLsModLoader.Log.LogDebug("SATONE animator read-back failed: " + inner.GetType().Name + ": " + inner.Message);
                return null;
            }
        }

        private static float ResolveFacialDelay()
        {
            try
            {
                var field = typeof(Game.MyDefine).GetField(
                    FacialDelayConstant, BindingFlags.Public | BindingFlags.Static);

                if (field is null || field.FieldType != typeof(float))
                {
                    _facialDelayDetail =
                        "MyDefine." + FacialDelayConstant + " is missing or is not a float; using "
                        + DefaultFacialResetDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture)
                        + " s from MyDefine";
                    CwyLsModLoader.Log.LogWarning("SATONE " + _facialDelayDetail);
                    return DefaultFacialResetDelaySeconds;
                }

                // A const is a literal in metadata: GetRawConstantValue reads the game's current
                // number instead of the one the compiler would have inlined into this assembly.
                var raw = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
                if (raw is not float seconds || seconds < 0f)
                {
                    _facialDelayDetail =
                        "MyDefine." + FacialDelayConstant + " read back as '" + raw + "'; using "
                        + DefaultFacialResetDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";
                    CwyLsModLoader.Log.LogWarning("SATONE " + _facialDelayDetail);
                    return DefaultFacialResetDelaySeconds;
                }

                _facialDelayDetail =
                    "MyDefine." + FacialDelayConstant + "=" + seconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";
                return seconds;
            }
            catch (Exception ex)
            {
                _facialDelayDetail =
                    "could not read MyDefine." + FacialDelayConstant + ": " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogWarning("SATONE " + _facialDelayDetail);
                return DefaultFacialResetDelaySeconds;
            }
        }

        private static void ResolveLookSeam()
        {
            try
            {
                var method = typeof(Game.HeroineService).GetMethod(
                    "ChangeLookScaleAnimation", BindingFlags.Public | BindingFlags.Instance);

                var parameters = method?.GetParameters();
                if (method is null || parameters is null || parameters.Length != 3 || !parameters[2].ParameterType.IsEnum)
                {
                    _lookSeamDetail =
                        "HeroineService.ChangeLookScaleAnimation(float, float, Ease) was not found "
                        + "with the expected shape; the look API is disabled";
                    CwyLsModLoader.Log.LogWarning("SATONE " + _lookSeamDetail);
                    return;
                }

                _changeLookScale = method;
                _easeUnset = Enum.ToObject(parameters[2].ParameterType, 0); // DG.Tweening.Ease.Unset
                _lookSeamDetail =
                    $"ChangeLookScaleAnimation(float, float, {parameters[2].ParameterType.FullName}) bound";
            }
            catch (Exception ex)
            {
                _lookSeamDetail = "could not bind ChangeLookScaleAnimation: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogWarning("SATONE " + _lookSeamDetail);
            }
        }

        private static void ResolveAnimatorSeam()
        {
            try
            {
                var animator = Type.GetType(AnimatorTypeName, throwOnError: false);
                if (animator is null)
                {
                    _animatorSeamDetail = $"'{AnimatorTypeName}' is not loaded; no read-back";
                    CwyLsModLoader.Log.LogDebug("SATONE " + _animatorSeamDetail);
                    return;
                }

                var getComponent = typeof(Game.HeroineService).GetMethod(
                    "GetComponent", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
                var getInteger = animator.GetMethod("GetInteger", new[] { typeof(string) });
                var getFloat = animator.GetMethod("GetFloat", new[] { typeof(string) });

                if (getComponent is null || getInteger is null || getFloat is null)
                {
                    _animatorSeamDetail = "Animator.GetInteger/GetFloat or Component.GetComponent(Type) is missing; no read-back";
                    CwyLsModLoader.Log.LogWarning("SATONE " + _animatorSeamDetail);
                    return;
                }

                _animatorType = animator;
                _getComponent = getComponent;
                _getInteger = getInteger;
                _getFloat = getFloat;
                _animatorSeamDetail = "Animator read-back bound via " + animator.Assembly.GetName().Name;
            }
            catch (Exception ex)
            {
                _animatorSeamDetail = "could not bind the Animator read-back: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogWarning("SATONE " + _animatorSeamDetail);
            }
        }
    }
}
