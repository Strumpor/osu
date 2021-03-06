// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.OpenGL.Textures;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Game.Audio;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Scoring;
using osuTK.Graphics;

namespace osu.Game.Skinning
{
    public class LegacySkin : Skin
    {
        [CanBeNull]
        protected TextureStore Textures;

        [CanBeNull]
        protected IResourceStore<SampleChannel> Samples;

        /// <summary>
        /// Whether texture for the keys exists.
        /// Used to determine if the mania ruleset is skinned.
        /// </summary>
        private readonly Lazy<bool> hasKeyTexture;

        protected virtual bool AllowManiaSkin => hasKeyTexture.Value;

        public new LegacySkinConfiguration Configuration
        {
            get => base.Configuration as LegacySkinConfiguration;
            set => base.Configuration = value;
        }

        private readonly Dictionary<int, LegacyManiaSkinConfiguration> maniaConfigurations = new Dictionary<int, LegacyManiaSkinConfiguration>();

        public LegacySkin(SkinInfo skin, IResourceStore<byte[]> storage, AudioManager audioManager)
            : this(skin, new LegacySkinResourceStore<SkinFileInfo>(skin, storage), audioManager, "skin.ini")
        {
        }

        protected LegacySkin(SkinInfo skin, IResourceStore<byte[]> storage, AudioManager audioManager, string filename)
            : base(skin)
        {
            using (var stream = storage?.GetStream(filename))
            {
                if (stream != null)
                {
                    using (LineBufferedReader reader = new LineBufferedReader(stream, true))
                        Configuration = new LegacySkinDecoder().Decode(reader);

                    stream.Seek(0, SeekOrigin.Begin);

                    using (LineBufferedReader reader = new LineBufferedReader(stream))
                    {
                        var maniaList = new LegacyManiaSkinDecoder().Decode(reader);

                        foreach (var config in maniaList)
                            maniaConfigurations[config.Keys] = config;
                    }
                }
                else
                    Configuration = new LegacySkinConfiguration();
            }

            if (storage != null)
            {
                var samples = audioManager?.GetSampleStore(storage);
                if (samples != null)
                    samples.PlaybackConcurrency = OsuGameBase.SAMPLE_CONCURRENCY;

                Samples = samples;
                Textures = new TextureStore(new TextureLoaderStore(storage));

                (storage as ResourceStore<byte[]>)?.AddExtension("ogg");
            }

            // todo: this shouldn't really be duplicated here (from ManiaLegacySkinTransformer). we need to come up with a better solution.
            hasKeyTexture = new Lazy<bool>(() => this.GetAnimation(
                lookupForMania<string>(new LegacyManiaSkinConfigurationLookup(4, LegacyManiaSkinConfigurationLookups.KeyImage, 0))?.Value ?? "mania-key1", true,
                true) != null);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            Textures?.Dispose();
            Samples?.Dispose();
        }

        public override IBindable<TValue> GetConfig<TLookup, TValue>(TLookup lookup)
        {
            switch (lookup)
            {
                case GlobalSkinColours colour:
                    switch (colour)
                    {
                        case GlobalSkinColours.ComboColours:
                            var comboColours = Configuration.ComboColours;
                            if (comboColours != null)
                                return SkinUtils.As<TValue>(new Bindable<IReadOnlyList<Color4>>(comboColours));

                            break;

                        default:
                            return SkinUtils.As<TValue>(getCustomColour(Configuration, colour.ToString()));
                    }

                    break;

                case LegacySkinConfiguration.LegacySetting legacy:
                    switch (legacy)
                    {
                        case LegacySkinConfiguration.LegacySetting.Version:
                            return SkinUtils.As<TValue>(new Bindable<decimal>(Configuration.LegacyVersion ?? LegacySkinConfiguration.LATEST_VERSION));
                    }

                    break;

                case SkinCustomColourLookup customColour:
                    return SkinUtils.As<TValue>(getCustomColour(Configuration, customColour.Lookup.ToString()));

                case LegacyManiaSkinConfigurationLookup maniaLookup:
                    if (!AllowManiaSkin)
                        return null;

                    var result = lookupForMania<TValue>(maniaLookup);
                    if (result != null)
                        return result;

                    break;

                default:
                    // handles lookups like GlobalSkinConfiguration

                    try
                    {
                        if (Configuration.ConfigDictionary.TryGetValue(lookup.ToString(), out var val))
                        {
                            // special case for handling skins which use 1 or 0 to signify a boolean state.
                            if (typeof(TValue) == typeof(bool))
                                val = val == "1" ? "true" : "false";

                            var bindable = new Bindable<TValue>();
                            if (val != null)
                                bindable.Parse(val);
                            return bindable;
                        }
                    }
                    catch
                    {
                    }

                    break;
            }

            return null;
        }

        private IBindable<TValue> lookupForMania<TValue>(LegacyManiaSkinConfigurationLookup maniaLookup)
        {
            if (!maniaConfigurations.TryGetValue(maniaLookup.Keys, out var existing))
                maniaConfigurations[maniaLookup.Keys] = existing = new LegacyManiaSkinConfiguration(maniaLookup.Keys);

            switch (maniaLookup.Lookup)
            {
                case LegacyManiaSkinConfigurationLookups.ColumnWidth:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(new Bindable<float>(existing.ColumnWidth[maniaLookup.TargetColumn.Value]));

                case LegacyManiaSkinConfigurationLookups.ColumnSpacing:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(new Bindable<float>(existing.ColumnSpacing[maniaLookup.TargetColumn.Value]));

                case LegacyManiaSkinConfigurationLookups.HitPosition:
                    return SkinUtils.As<TValue>(new Bindable<float>(existing.HitPosition));

                case LegacyManiaSkinConfigurationLookups.LightPosition:
                    return SkinUtils.As<TValue>(new Bindable<float>(existing.LightPosition));

                case LegacyManiaSkinConfigurationLookups.ShowJudgementLine:
                    return SkinUtils.As<TValue>(new Bindable<bool>(existing.ShowJudgementLine));

                case LegacyManiaSkinConfigurationLookups.ExplosionScale:
                    Debug.Assert(maniaLookup.TargetColumn != null);

                    if (GetConfig<LegacySkinConfiguration.LegacySetting, decimal>(LegacySkinConfiguration.LegacySetting.Version)?.Value < 2.5m)
                        return SkinUtils.As<TValue>(new Bindable<float>(1));

                    if (existing.ExplosionWidth[maniaLookup.TargetColumn.Value] != 0)
                        return SkinUtils.As<TValue>(new Bindable<float>(existing.ExplosionWidth[maniaLookup.TargetColumn.Value] / LegacyManiaSkinConfiguration.DEFAULT_COLUMN_SIZE));

                    return SkinUtils.As<TValue>(new Bindable<float>(existing.ColumnWidth[maniaLookup.TargetColumn.Value] / LegacyManiaSkinConfiguration.DEFAULT_COLUMN_SIZE));

                case LegacyManiaSkinConfigurationLookups.ColumnLineColour:
                    return SkinUtils.As<TValue>(getCustomColour(existing, "ColourColumnLine"));

                case LegacyManiaSkinConfigurationLookups.JudgementLineColour:
                    return SkinUtils.As<TValue>(getCustomColour(existing, "ColourJudgementLine"));

                case LegacyManiaSkinConfigurationLookups.ColumnBackgroundColour:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(getCustomColour(existing, $"Colour{maniaLookup.TargetColumn + 1}"));

                case LegacyManiaSkinConfigurationLookups.ColumnLightColour:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(getCustomColour(existing, $"ColourLight{maniaLookup.TargetColumn + 1}"));

                case LegacyManiaSkinConfigurationLookups.MinimumColumnWidth:
                    return SkinUtils.As<TValue>(new Bindable<float>(existing.MinimumColumnWidth));

                case LegacyManiaSkinConfigurationLookups.NoteImage:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(getManiaImage(existing, $"NoteImage{maniaLookup.TargetColumn}"));

                case LegacyManiaSkinConfigurationLookups.HoldNoteHeadImage:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(getManiaImage(existing, $"NoteImage{maniaLookup.TargetColumn}H"));

                case LegacyManiaSkinConfigurationLookups.HoldNoteTailImage:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(getManiaImage(existing, $"NoteImage{maniaLookup.TargetColumn}T"));

                case LegacyManiaSkinConfigurationLookups.HoldNoteBodyImage:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(getManiaImage(existing, $"NoteImage{maniaLookup.TargetColumn}L"));

                case LegacyManiaSkinConfigurationLookups.KeyImage:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(getManiaImage(existing, $"KeyImage{maniaLookup.TargetColumn}"));

                case LegacyManiaSkinConfigurationLookups.KeyImageDown:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(getManiaImage(existing, $"KeyImage{maniaLookup.TargetColumn}D"));

                case LegacyManiaSkinConfigurationLookups.LeftStageImage:
                    return SkinUtils.As<TValue>(getManiaImage(existing, "StageLeft"));

                case LegacyManiaSkinConfigurationLookups.RightStageImage:
                    return SkinUtils.As<TValue>(getManiaImage(existing, "StageRight"));

                case LegacyManiaSkinConfigurationLookups.BottomStageImage:
                    return SkinUtils.As<TValue>(getManiaImage(existing, "StageBottom"));

                case LegacyManiaSkinConfigurationLookups.LightImage:
                    return SkinUtils.As<TValue>(getManiaImage(existing, "StageLight"));

                case LegacyManiaSkinConfigurationLookups.HitTargetImage:
                    return SkinUtils.As<TValue>(getManiaImage(existing, "StageHint"));

                case LegacyManiaSkinConfigurationLookups.LeftLineWidth:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(new Bindable<float>(existing.ColumnLineWidth[maniaLookup.TargetColumn.Value]));

                case LegacyManiaSkinConfigurationLookups.RightLineWidth:
                    Debug.Assert(maniaLookup.TargetColumn != null);
                    return SkinUtils.As<TValue>(new Bindable<float>(existing.ColumnLineWidth[maniaLookup.TargetColumn.Value + 1]));

                case LegacyManiaSkinConfigurationLookups.Hit0:
                case LegacyManiaSkinConfigurationLookups.Hit50:
                case LegacyManiaSkinConfigurationLookups.Hit100:
                case LegacyManiaSkinConfigurationLookups.Hit200:
                case LegacyManiaSkinConfigurationLookups.Hit300:
                case LegacyManiaSkinConfigurationLookups.Hit300g:
                    return SkinUtils.As<TValue>(getManiaImage(existing, maniaLookup.Lookup.ToString()));
            }

            return null;
        }

        private IBindable<Color4> getCustomColour(IHasCustomColours source, string lookup)
            => source.CustomColours.TryGetValue(lookup, out var col) ? new Bindable<Color4>(col) : null;

        private IBindable<string> getManiaImage(LegacyManiaSkinConfiguration source, string lookup)
            => source.ImageLookups.TryGetValue(lookup, out var image) ? new Bindable<string>(image) : null;

        public override Drawable GetDrawableComponent(ISkinComponent component)
        {
            switch (component)
            {
                case GameplaySkinComponent<HitResult> resultComponent:
                    switch (resultComponent.Component)
                    {
                        case HitResult.Miss:
                            return this.GetAnimation("hit0", true, false);

                        case HitResult.Meh:
                            return this.GetAnimation("hit50", true, false);

                        case HitResult.Good:
                            return this.GetAnimation("hit100", true, false);

                        case HitResult.Great:
                            return this.GetAnimation("hit300", true, false);
                    }

                    break;
            }

            return this.GetAnimation(component.LookupName, false, false);
        }

        public override Texture GetTexture(string componentName, WrapMode wrapModeS, WrapMode wrapModeT)
        {
            foreach (var name in getFallbackNames(componentName))
            {
                float ratio = 2;
                var texture = Textures?.Get($"{name}@2x", wrapModeS, wrapModeT);

                if (texture == null)
                {
                    ratio = 1;
                    texture = Textures?.Get(name, wrapModeS, wrapModeT);
                }

                if (texture == null)
                    continue;

                texture.ScaleAdjust = ratio;
                return texture;
            }

            return null;
        }

        public override SampleChannel GetSample(ISampleInfo sampleInfo)
        {
            foreach (var lookup in sampleInfo.LookupNames)
            {
                var sample = Samples?.Get(lookup);

                if (sample != null)
                    return sample;
            }

            if (sampleInfo is HitSampleInfo hsi)
                // Try fallback to non-bank samples.
                return Samples?.Get(hsi.Name);

            return null;
        }

        private IEnumerable<string> getFallbackNames(string componentName)
        {
            // May be something like "Gameplay/osu/approachcircle" from lazer, or "Arrows/note1" from a user skin.
            yield return componentName;

            // Fall back to using the last piece for components coming from lazer (e.g. "Gameplay/osu/approachcircle" -> "approachcircle").
            string lastPiece = componentName.Split('/').Last();
            yield return componentName.StartsWith("Gameplay/taiko/") ? "taiko-" + lastPiece : lastPiece;
        }
    }
}
