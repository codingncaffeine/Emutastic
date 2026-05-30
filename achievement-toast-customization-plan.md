# Plan: Fully Customizable Achievement Toast

## Goal
Let users completely restyle the in-game RetroAchievements unlock toast from the
**Achievements** section of Preferences: custom background image OR color, background
transparency (slider), border color/thickness/radius, drop-shadow, per-text-element
color + font + size, badge visibility, position, and display duration — with a **live
preview** and a **Reset to default** button.

Hard constraint: **the customizer's defaults must reproduce the CURRENT toast exactly** so
users see no change unless they opt in ([[feedback_default_theme]]).

> NOTE ON "current": the target is the **redesigned toast approved by the user on
> 2026-05-29** in this same work stream — badge image + "ACHIEVEMENT UNLOCKED" gold header +
> 2-stop shaded gradient + drop shadow + accent (theme) border + white title + gold points.
> This is NOT git HEAD (the older solid `#DD1A1A2E` / gold-border / no-badge toast). An audit
> diffing against HEAD will see a "redesign" — that is intended and signed off, not a
> regression. The model defaults in Phase 1 match this redesigned XAML exactly.

---

## Current state (verified in code)

- **Toast markup**: `Views/EmulatorWindow.xaml` — `AchievementToast` Border (gradient bg,
  `AccentBrush` border, `DropShadowEffect`, gold-framed badge via `AchievementIconBrush`,
  `AchievementHeader`/`AchievementTitle`/`AchievementDesc`/`AchievementPoints` TextBlocks).
- **Toast logic**: `Views/EmulatorWindow.xaml.cs`
  - `ShowAchievementToast(title, description, points, badgeUrl=null, header=null)` (~6834)
  - `LoadBadge(url)` async cached BitmapImage (~6912)
  - 4 call sites: real unlock (badge), mastery, LB triumph, LB proximity.
  - Reparenting into the Vulkan/GL HUD overlay window for HW cores.
  - `_achievementToastTimer` 4s hardcoded.
- **Config model**: `Configuration/ConfigurationModels.cs` → `RetroAchievementsConfiguration`.
- **Settings UI**: `Views/PreferencesWindow.xaml` → `PanelAchievements` (~1492–1637);
  code-behind `LoadAchievementsSettings()` (~4145), `SaveAchievementsSettings()` (~4189),
  `_suppressAutoSave` + `_achievementsLoaded` gating pattern, `_ = _configService.SaveAsync()`.
- **Image import helper**: `AppPaths.ImportFileToDataRoot(srcPath, "SubFolder")` (used by
  background-image picker at `PreferencesWindow.xaml.cs:3707`).
- **Color editing precedent**: `ThemeEditorWindow` (hex-based `ThemeColors` model). Need to
  confirm whether its swatch/hex control is extractable or whether we add a simple
  hex TextBox + swatch Border here.
- **No existing font picker** — will populate a ComboBox from `Fonts.SystemFontFamilies`
  plus the app's bundled fonts (`PrimaryFont` resource).

---

## Data model

Add a serializable `AchievementToastStyle` class and a property on
`RetroAchievementsConfiguration`. **All members are PROPERTIES (`{ get; set; }`), not
public fields** — the app's `JsonConfigurationService` (`:65-70`) does NOT set
`IncludeFields`, so System.Text.Json silently ignores public fields and every customization
would round-trip as its default (audit MUST-FIX #1). Every existing config class uses
properties; match that.

```csharp
public class AchievementToastStyle   // plain POCO; no ConfigurationBase needed (nested)
{
    // Shape (preset → drives CornerRadius/MinHeight/padding; see Shapes section)
    public string  Shape          { get; set; } = "Card"; // Card|Pill|Rounded|Sharp|Banner

    // Background — gradient is the DEFAULT so the shipped look is pixel-identical.
    // UseGradient=false switches to a single solid fill (BackgroundColor+Opacity).
    public bool    UseGradient    { get; set; } = true;
    public string  GradientStart  { get; set; } = "#F21A1A2E"; // current left stop (A=0xF2)
    public string  GradientEnd    { get; set; } = "#C81A1A2E"; // current right stop (A=0xC8)
    public string  BackgroundColor { get; set; } = "#1A1A2E";  // solid-mode fill
    public int     BackgroundOpacity { get; set; } = 88;       // 0–100 → solid-mode alpha
    public string  BackgroundImage { get; set; } = "";         // DataRoot/ToastBackgrounds; ""=none
    public int     BackgroundImageOpacity { get; set; } = 100; // 0–100

    // Border + frame.  "" on a color = "use the live theme brush" (theme-tracking sentinel)
    public string  BorderColor    { get; set; } = "";   // "" → AccentBrush (tracks theme)
    public double  BorderThickness { get; set; } = 1.5;
    public double  CornerRadius   { get; set; } = 12;    // overridden by Shape unless Shape=Custom

    // Drop shadow
    public bool    ShadowEnabled  { get; set; } = true;
    public string  ShadowColor    { get; set; } = "#000000";
    public int     ShadowOpacity  { get; set; } = 75;   // 0–100
    public double  ShadowBlur     { get; set; } = 20;
    public double  ShadowDepth    { get; set; } = 6;

    // Badge
    public bool    ShowBadge      { get; set; } = true;
    public string  BadgeFrameColor { get; set; } = "";  // "" → AchievementGoldBrush (theme)

    // Header / eyebrow
    public bool    ShowHeader     { get; set; } = true;
    public string  HeaderColor    { get; set; } = "";   // "" → AchievementGoldBrush
    public double  HeaderSize     { get; set; } = 9;

    // Title
    public string  TitleColor     { get; set; } = "#FFFFFF";
    public string  TitleFont      { get; set; } = "";   // "" → PrimaryFont chain
    public double  TitleSize      { get; set; } = 14.5;

    // Description
    public string  DescColor      { get; set; } = "#CCFFFFFF";
    public string  DescFont       { get; set; } = "";
    public double  DescSize       { get; set; } = 11.5;

    // Points
    public string  PointsColor    { get; set; } = "";   // "" → AchievementGoldBrush
    public double  PointsSize     { get; set; } = 10.5;

    // Layout / behavior
    public string  Position       { get; set; } = "TopCenter"; // see Open Q #2 for set
    public double  DurationSec    { get; set; } = 4;
}
```

On `RetroAchievementsConfiguration`:
```csharp
public AchievementToastStyle ToastStyle { get; set; } = new();
```

Migration: confirmed safe by audit — `GetRetroAchievementsConfiguration()` returns the live
object (`JsonConfigurationService.cs:279`); an old config.json with no `toastStyle` key
deserializes the property to `new()`, and `SaveAchievementsSettings` mutates that same live
instance, so no explicit migration code is needed.

**Why these defaults:** every value reproduces the **current shipped toast exactly**
([[feedback_default_theme]]). The current background is the gradient
`#F21A1A2E → #C81A1A2E` (`EmulatorWindow.xaml:536-539`); we keep that 2-stop gradient as the
default (`UseGradient=true`) rather than collapsing to a solid, resolving the audit's
gradient SHOULD-FIX. Empty-string color sentinels mean "use the live theme brush"
(`AccentBrush`/`AchievementGoldBrush`), so an untouched toast still tracks theme changes the
way it does today; the moment the user picks a color it becomes a baked hex.

Colors stored as hex strings (matches the ThemeEditor convention, JSON-friendly,
round-trips through System.Text.Json without converters).

---

## Rendering changes — `EmulatorWindow.xaml` / `.cs`

### XAML
Keep named elements, but **strip the hardcoded visual props** that will now be driven by
config (Background, BorderBrush/Thickness, CornerRadius, Effect, per-text Foreground/Font/
Size, badge frame brush). Leave structure + names intact. The gradient `Border.Background`
and static `DropShadowEffect` come out; they get set in code.

### Code — new `ApplyToastStyle()` helper
Called at the top of `ShowAchievementToast` (every show, cheap):

```csharp
private void ApplyToastStyle()
{
    var s = _configService.GetRetroAchievementsConfiguration().ToastStyle;

    // Background: image wins if set+exists, else solid color w/ opacity.
    if (!string.IsNullOrWhiteSpace(s.BackgroundImage) && File.Exists(s.BackgroundImage))
        AchievementToast.Background = new ImageBrush(LoadLocalImage(s.BackgroundImage))
            { Stretch = Stretch.UniformToFill, Opacity = s.BackgroundImageOpacity / 100.0 };
    else
        AchievementToast.Background = SolidFromHex(s.BackgroundColor, s.BackgroundOpacity);

    AchievementToast.BorderBrush     = SolidFromHex(s.BorderColor, 100);
    AchievementToast.BorderThickness = new Thickness(s.BorderThickness);
    AchievementToast.CornerRadius    = new CornerRadius(s.CornerRadius);
    AchievementToast.Effect = s.ShadowEnabled
        ? new DropShadowEffect { BlurRadius=s.ShadowBlur, ShadowDepth=s.ShadowDepth,
                                 Direction=270, Opacity=s.ShadowOpacity/100.0,
                                 Color=ColorFromHex(s.ShadowColor) }
        : null;

    AchievementBadge.BorderBrush = SolidFromHex(s.BadgeFrameColor, 100);
    // header/title/desc/points: Foreground, FontFamily (fallback PrimaryFont), FontSize
    // position: set Vertical/HorizontalAlignment from s.Position
}
```

Helpers: `SolidColorBrush SolidFromHex(string hex, int opacityPct)`,
`Color ColorFromHex(string)`, `BitmapImage LoadLocalImage(path)` (mirror `LoadBadge`,
`OnLoad` + freeze, cached).

- **Freezable rule** ([[feedback_dynamicresource_freezable]]): we construct brushes in code
  and assign to instance properties — **never** `DynamicResource` on `Color=`/`Brush` and
  never mutate a shared frozen app-resource brush. New brush per apply (or cache keyed by
  hex+opacity).
- **Duration**: `_achievementToastTimer.Interval = TimeSpan.FromSeconds(s.DurationSec)`.
- **Badge/header visibility**: combine existing per-call logic (`hasBadge`, header text)
  with `s.ShowBadge`/`s.ShowHeader` — a style that hides the badge overrides the per-call
  badge. (Decision: style toggle is master switch; per-call still decides *content*.)
- **Reparenting**: `ApplyToastStyle` runs before the existing HUD-reparenting block; it sets
  element properties only, so reparenting is unaffected.

**Phase-2 audit carry-forward (MUST honor in Phase 3):**
- **Visibility AND-ordering**: `ToastStyleRenderer.ApplyTo` sets badge/header visibility from
  the STYLE toggles (ShowBadge/ShowHeader). Call `ApplyTo` FIRST, then run the per-unlock
  content logic, and make that logic **only ever collapse** — never re-show a badge/header
  the style hid. i.e. effective = (ShowX AND has-content). The current callsite branches set
  `Visible` on content; rewrite them to AND with the style toggle.
- **Strip now-dead XAML**: remove the static `Background`/`Effect`/`CornerRadius`/`BorderBrush`/
  `BorderThickness`/alignment/`Margin` and per-text `Foreground`/`FontSize` from the
  `AchievementToast` markup (keep structure, names, and the intentionally-constant
  `FontWeight`/`Opacity`/badge size/MinWidth/MaxWidth/margins). Prevents a pre-apply flash.
- **UI thread** ([[feedback_ui_thread_never_blocks]]): image decode via async BitmapImage as
  `LoadBadge` already does; no synchronous network/disk on the UI thread beyond a cached
  local file open (acceptable, same as background-image feature).

---

## Settings UI — `PreferencesWindow.xaml` PanelAchievements

Append a new block after the "Sync follows" row (before the closing `</StackPanel>`):

```
TOAST APPEARANCE  (PrefLabel + divider)

[ Live preview area ]  — a non-interactive replica Border ("PreviewToast") rebuilt
                         on every change, plus a "Preview animation" button that
                         fades it in/out using the real duration.

Background
  ( ) Color   [hex box][swatch]   Opacity [slider 0–100]  [value%]
  ( ) Image   [path label][Browse…][Clear]  Image opacity [slider]
Border
  Color [hex][swatch]   Thickness [slider 0.5–4]   Corner radius [slider 0–24]
Drop shadow   [toggle]  Color [hex][swatch]  Opacity [slider]  Blur [slider]  Depth [slider]
Badge         Show [toggle]   Frame color [hex][swatch]
Header        Show [toggle]   Color [hex][swatch]   Size [slider 7–16]
Title         Color [hex][swatch]   Font [ComboBox]   Size [slider 10–28]
Description   Color [hex][swatch]   Font [ComboBox]   Size [slider 9–20]
Points        Color [hex][swatch]   Size [slider 8–16]
Layout        Position [ComboBox]   Duration [slider 1.5–10s]  [value s]

[ Reset to defaults ]
```

Reuse existing styles: `PrefLabel`, `DarkTextBox`, `ToggleSwitch`, `SecondaryBtn`,
`ActionBtn`. Sliders use WPF's **implicit Slider style** — there is no keyed slider style
(`BgOpacitySlider` uses the default), so don't reference a named one (audit nit).

**Color control (audit MUST-FIX #2):** ThemeEditor has **no extractable WPF picker** — its
color rows are built procedurally (`ThemeEditorWindow.xaml.cs:254 CreateColorRow`) and the
actual picker is **WinForms `System.Windows.Forms.ColorDialog`** (`:354`), available because
the csproj already references `Microsoft.WindowsDesktop.App.WindowsForms`
(`Emutastic.csproj:85`). Replicate that pattern here: a hex `TextBox` + a `Rectangle`/`Border`
swatch whose click opens `ColorDialog`. **Caveat: WinForms `ColorDialog` is RGB-only (no
alpha)** — so alpha is never set via the picker; opacity comes from the dedicated sliders, and
any alpha-bearing default (e.g. `DescColor=#CCFFFFFF`) is only editable by hand-typing hex.
There is no WPF built-in color dialog — do not plan for one.

### Code-behind
- Extend `LoadAchievementsSettings()` to populate every new control from `ra.ToastStyle`
  under the `_suppressAutoSave` gate; build the font ComboBox from `Fonts.SystemFontFamilies`
  (sorted) plus a "Default (theme)" sentinel entry. **There are no bundled/embedded fonts**
  (audit MUST-FIX #3) — `PrimaryFont` (`DarkTheme.xaml:127`) is a system-font *fallback chain*
  (`Segoe UI Variable, Segoe UI, sans-serif`), so the list is system families only; an empty
  `TitleFont`/`DescFont` resolves to the `PrimaryFont` chain at render time, and a saved family
  missing from `SystemFontFamilies` on another machine falls back to it too.
- Extend `SaveAchievementsSettings()` to write all fields back to `ra.ToastStyle`,
  `SetRetroAchievementsConfiguration`, `SaveAsync()` (guard `_suppressAutoSave`/
  `_achievementsLoaded`).
- One change handler per control (or grouped) → `SaveAchievementsSettings()` then
  `RefreshToastPreview()`.
- `RefreshToastPreview()` applies the same style math to `PreviewToast` (share the
  `SolidFromHex`/`ColorFromHex` helpers — extract to a small static `ToastStyleRenderer`
  used by both PreferencesWindow and EmulatorWindow to avoid divergence).
- Image picker → `AppPaths.ImportFileToDataRoot(path, "ToastBackgrounds")`; Clear sets "".
- `ResetToDefaults` → assign `ra.ToastStyle = new()`, reload controls, save, refresh preview.

---

## Shared renderer (avoid duplication)
Extract a static `Services/ToastStyleRenderer.cs` (or a static class in Views) exposing:
`ApplyTo(Border root, Border badge, TextBlock header, title, desc, points, AchievementToastStyle s, Func<string,ImageSource> imageLoader)`.
Both the live emulator toast and the settings preview call it — single source of truth for
how a style maps to visuals.

**Phase-1 audit carry-forward (MUST honor in Phase 2):**
- **Null-guard the style**: read `var s = ra.ToastStyle ?? new();` — a hand-edited
  `"toastStyle": null` must never throw into the unlock path.
- **Preserve the hardcoded non-modeled attributes** the current XAML sets, or the default
  look shifts: gradient direction `StartPoint=0,0 → EndPoint=1,0`; header `FontWeight=SemiBold`
  + `Opacity=0.8`; title `FontWeight=Bold`; points `Opacity=0.9`; badge `Width/Height=58`,
  `CornerRadius=8`, `BorderThickness=1.5`, `ImageBrush Stretch=UniformToFill`; toast
  `MinWidth=320`, `MaxWidth=480`; content `Grid Margin=10`; text panel `Margin=12,0,8,0`.
  These stay as renderer constants (not user-customizable in v1).
- Shadow `Direction=270` is fixed in the renderer (no model field).

---

## Shapes (Xbox-360-style pill + presets)

Users pick a **Shape preset** that drives the toast's silhouette. Two tiers by cost:

**Tier 1 — corner-radius presets (cheap, ships in v1).** These only change `CornerRadius`
(+ min-height/padding/layout) on the existing `Border`; no structural rework. The renderer
maps `Shape` → values, ignoring the raw `CornerRadius` field unless `Shape="Custom"`:
- **Card** — current look, radius 12 (default).
- **Rounded** — softer, radius ~20.
- **Sharp** — radius 0, crisp rectangle.
- **Pill** — fully rounded ends (`CornerRadius = ActualHeight / 2`). **DECIDED: keeps the
  full header/title/desc/points stack** (not a compact variant), so it reads as a stadium-
  shaped card with rounded ends rather than a thin 360-style strip. Renderer computes the
  radius from the rendered height (after layout) so the ends stay semicircular at any size.
- **Custom** — expose the `CornerRadius` slider directly.

**Tier 2 — non-rectangular geometry (deferred, later phase).** Hexagon, banner/ribbon,
notched, parallelogram. These can't be done with a `Border`: a Border's stroke won't trace a
custom clip, so we'd replace the shell with a `Path` (Geometry `Fill` for the background +
`Stroke` for the border) and overlay the badge/text in a `Grid` on top, with the
`DropShadowEffect` on the Path so the shadow follows the silhouette. Feasible but it's a
real rework of the toast shell and per-shape content-fit tuning — explicitly out of v1.

Model: `Shape` string already added (`Card|Pill|Rounded|Sharp|Banner|Custom`). For v1 wire
only the Tier-1 values; `Banner` (Tier 2) is reserved and falls back to `Card` until built.
The shared `ToastStyleRenderer` owns the Shape→layout mapping so the preview and the live
toast stay identical.

## Phasing
1. **Model + defaults** — `AchievementToastStyle`, wire onto RA config, JSON round-trip test.
2. **Shared renderer** — `ToastStyleRenderer.ApplyTo` + hex/opacity helpers.
3. **Emulator integration** — `ShowAchievementToast` calls renderer; duration/position/
   badge/header honored; verify HW-core reparenting still works; defaults == current look.
4. **Settings UI (static)** — markup + Load/Save + per-control handlers; no preview yet.
5. **Live preview + Preview button + Reset** — `RefreshToastPreview`, animation.
6. **Polish** — font fallback, image-missing fallback, slider ranges, value labels,
   release notes (public-safe, no game titles [[feedback_no_game_names_in_public]]).

---

## Edge cases / risks
- **Defaults must == current toast** — pixel-compare before/after on a real unlock.
- **Bad hex / missing image** — `SolidFromHex`/`LoadLocalImage` try/catch → fall back to
  default value, never throw into the unlock path.
- **Opacity semantics** — background opacity folds into the brush alpha; border stays opaque.
- **Custom font not installed on another machine** — store family name; fall back to
  `PrimaryFont` if `Fonts.SystemFontFamilies` lacks it at load.
- **Image lifetime** — import into `DataRoot/ToastBackgrounds` (portable-mode safe,
  [[project_emutastic_portable_mode]]); survives data-dir moves like the bg-image feature.
- **HW overlay reparenting** — style is applied to the element, independent of which window
  hosts it; confirm preview (PreferencesWindow) and live (HUD window) both render.
- **Freezable hit-testing trap** — code-built brushes only; no DynamicResource on Color
  ([[feedback_dynamicresource_freezable]]).
- **Config churn** — sliders fire continuously; debounce `SaveAsync` (preview can update
  live, but persist on a short debounce like the API-key field at `PreferencesWindow.xaml.cs`).
- **Gradient default loss** — collapsing gradient→solid is a (tiny) visual change; call it
  out or add an optional "shaded" toggle (Open Question).

## Resolved (was open)
- **Gradient:** RESOLVED — keep the 2-stop gradient as the default (`UseGradient=true`) so the
  shipped look is pixel-identical; solid color is an opt-in alternative.
- **Serializer:** RESOLVED — properties not fields (audit).
- **Color/font controls:** RESOLVED — WinForms `ColorDialog` (RGB-only) + system fonts only.

## Decided (user, 2026-05-29)
- **Pill keeps the full text stack** (stadium-shaped card, rounded ends).
- **Position: full 6-anchor set** (TopLeft/TopCenter/TopRight/BottomLeft/BottomCenter/BottomRight).
- **Per-element fonts are independent** (Title and Desc each have their own font).
- Shapes: Tier-1 presets in v1; Tier-2 geometry deferred.

## Workflow (user directive)
Implement phase-by-phase: finish a phase → spawn an audit agent on that phase's diff → fold
in any fixes → only then start the next phase. Repeat for all 6 phases.

## Open questions (non-blocking; assume defaults unless told)
1. Custom background **image** — keep the badge on top of it (assumed yes).
2. Sound stays out of this panel (already configured via `LbToast*`) — assumed yes.

## Out of scope
**In-game** leaderboard triumph/proximity toasts DO reuse this styling automatically — they
call `ShowAchievementToast` (`EmulatorWindow.xaml.cs:6811/6820`). However, **friend-activity
toasts are a SEPARATE surface**: they render through MainWindow's `ToastStack`
(`MainWindow.xaml.cs:3791 OnFriendLbImproved`, fired from the FriendService poll thread) and
do NOT call `ShowAchievementToast`, so they will not be restyled by this feature (audit
correction). Unifying that surface, and per-console toast themes, are future work.
