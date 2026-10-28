using System;
using System.Globalization;
using System.IO;
using CwyLs.ModLoader.Core.Grading;
using CwyLs.ModLoader.Loaders;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>
    /// Bakes the ColorLookup LUT: the preset's HSL adjustment and its LUT file (a <c>.cube</c> or a
    /// strip <c>.png</c>) composed into one strip texture at the pipeline's LUT size, cached by
    /// content so a re-apply that changes neither bakes nothing.
    /// <para>
    /// The texture is linear (URP's <c>ColorLookup.ValidateLUT</c> refuses sRGB formats) and
    /// <c>size x size</c> wide by <c>size</c> high (it refuses any other height than the pipeline's
    /// <c>colorGradingLutSize</c>, 32 in every <c>PC_URP-*.asset</c> of this game). Values are
    /// sRGB-encoded: URP converts to sRGB before the lookup and back after it.
    /// </para>
    /// </summary>
    internal static class GradingLutTexture
    {
        private static string _key = string.Empty;
        private static Texture2D? _texture;

        /// <summary>The cached texture, or null.</summary>
        internal static Texture2D? Current => _texture;

        /// <summary>
        /// The LUT for a plan, baked when its inputs changed. Main thread.
        /// </summary>
        /// <param name="plan">A plan with <see cref="GradingPlan.NeedsLut"/> true.</param>
        /// <param name="presetFolder">Folder the preset file is in (LUT paths are relative to it); null when the preset has no file.</param>
        /// <param name="size">The pipeline's LUT size.</param>
        /// <param name="problem">Why the LUT file could not be used (the HSL part is still baked), or null.</param>
        /// <returns>The texture, or null when there is nothing to bake.</returns>
        internal static Texture2D? Get(GradingPlan plan, string? presetFolder, int size, out string? problem)
        {
            problem = null;
            string? path = null;

            if (plan.LutFile != null && plan.LutAmount > 0)
            {
                if (presetFolder is null)
                {
                    problem = "lut.file \"" + plan.LutFile + "\" has no folder to be relative to (save the preset first)";
                }
                else
                {
                    path = Path.Combine(presetFolder, plan.LutFile.Replace('/', Path.DirectorySeparatorChar));
                }
            }

            if (plan.Hsl is null && path is null)
            {
                return null;
            }

            // The key covers everything the bake reads: HSL values, LUT file and its write time, amount, size.
            var key = (path is null ? "nofile" : path + "@" + Stamp(path)) + "|" +
                      (plan.Hsl?.CacheKey() ?? "nohsl") + "|" +
                      plan.LutAmount.ToString("R", CultureInfo.InvariantCulture) + "|" +
                      size.ToString(CultureInfo.InvariantCulture);
            if (key == _key && _texture != null)
            {
                return _texture;
            }

            var file = path is null ? null : Load(path, out problem);
            var hsl = plan.Hsl;
            if (hsl is null && file is null)
            {
                return null;
            }

            var amount = plan.LutAmount;
            var lut = Lut3D.Bake(size, c =>
            {
                var x = hsl is null ? c : hsl.Apply(c);
                if (file != null)
                {
                    x = Rgb.Lerp(x, file.Sample(x), amount);
                }

                return x.Clamp01();
            });

            var texture = ToTexture(lut);
            var old = _texture;
            _texture = texture;
            _key = key;
            DeferDestroy(old);
            return texture;
        }

        /// <summary>Destroys the cached texture now (teardown).</summary>
        internal static void Release()
        {
            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
            }

            _texture = null;
            _key = string.Empty;
        }

        private static string Stamp(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) : "missing";
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("GRADING: could not stat " + path + ": " + ex.GetType().Name + ": " + ex.Message);
                return "unreadable";
            }
        }

        private static Lut3D? Load(string path, out string? problem)
        {
            problem = null;
            try
            {
                if (!File.Exists(path))
                {
                    problem = "LUT file not found: " + path;
                    return null;
                }

                if (path.EndsWith(GradingFormat.CubeExtension, StringComparison.OrdinalIgnoreCase))
                {
                    var cube = Lut3D.ParseCube(File.ReadAllText(path), out var cubeProblem);
                    problem = cubeProblem is null ? null : Path.GetFileName(path) + ": " + cubeProblem;
                    return cube;
                }

                var image = TextureLoader.Load(path, new TextureLoadOptions(linear: true, mipMaps: false, readableAfterLoad: true));
                if (image == null)
                {
                    problem = "the strip LUT image could not be loaded: " + path;
                    return null;
                }

                var pixels = image.GetPixels();
                var rgba = new float[pixels.Length * 4];
                for (var i = 0; i < pixels.Length; i++)
                {
                    rgba[(i * 4) + 0] = pixels[i].r;
                    rgba[(i * 4) + 1] = pixels[i].g;
                    rgba[(i * 4) + 2] = pixels[i].b;
                    rgba[(i * 4) + 3] = pixels[i].a;
                }

                var strip = Lut3D.FromStrip(image.width, image.height, rgba, out var stripProblem);
                problem = stripProblem is null ? null : Path.GetFileName(path) + ": " + stripProblem;
                return strip;
            }
            catch (Exception ex)
            {
                problem = "the LUT file could not be read: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogDebug("GRADING: " + problem);
                return null;
            }
        }

        private static Texture2D ToTexture(Lut3D lut)
        {
            var n = lut.Size;
            var format = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf) ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
            var texture = new Texture2D(n * n, n, format, mipChain: false, linear: true)
            {
                name = "CWY.Grading.LUT",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
                hideFlags = HideFlags.DontSave,
            };

            var strip = lut.ToStripRgba();
            var colors = new Color[n * n * n];
            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] = new Color(strip[i * 4], strip[(i * 4) + 1], strip[(i * 4) + 2], strip[(i * 4) + 3]);
            }

            texture.SetPixels(colors);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }

        private static void DeferDestroy(Texture2D? texture)
        {
            if (texture == null)
            {
                return;
            }

            var work = MainThread.PostAfter(GradingVolume.ReleaseDelaySeconds, "grading.lut.free", () => UnityEngine.Object.Destroy(texture));
            if (!work.IsAccepted)
            {
                UnityEngine.Object.Destroy(texture);
            }
        }
    }
}
