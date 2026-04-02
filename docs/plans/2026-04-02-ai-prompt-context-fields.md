# AI Prompt Context Fields Implementation Plan

**Goal:** Let users configure habitat description, location, and species hint fields that are assembled into the AI recognition system prompt, with a live preview of the first 4 lines shown in Detection Settings.

**Architecture:** Two new `AppConfiguration` fields (`AiHabitatDescription`, `AiTargetSpeciesHint`) plus the existing `LocationName` are assembled at call time by a shared static `PromptBuilder` class, replacing the `const string SystemPrompt` in both recognition services. `DetectionSettingsViewModel` exposes the three editable fields plus a computed `PromptPreview` string that updates live. The UI in `DetectionSettingsWindow` adds a small "AI Prompt" sub-section inside the existing AI Recognition section.

**Tech Stack:** C# 12, WPF, CommunityToolkit.Mvvm, .NET 8

---

## Progress

- [x] Task 1: AppConfiguration fields + PromptBuilder + recognition services
- [x] Task 2: DetectionSettingsViewModel properties + DetectionSettingsWindow UI

---

## Files

- Modify: `src/WildlifeWatcher/WildlifeWatcher/Models/AppConfiguration.cs` — add `AiHabitatDescription` and `AiTargetSpeciesHint`
- Create: `src/WildlifeWatcher/WildlifeWatcher/Services/PromptBuilder.cs` — static helper that assembles the system prompt from settings
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Services/ClaudeRecognitionService.cs` — replace `const SystemPrompt` with `PromptBuilder.Build(_settings.CurrentSettings)`
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Services/GeminiRecognitionService.cs` — same
- Modify: `src/WildlifeWatcher/WildlifeWatcher/ViewModels/DetectionSettingsViewModel.cs` — add two observable properties + `PromptPreview` + auto-save wiring
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Views/DetectionSettingsWindow.xaml` — add "AI Prompt" sub-section with fields and preview
- Modify: `docs/recognition-pipeline.md` — update Step 5 to describe dynamic prompt construction

---

### Task 1: AppConfiguration fields + PromptBuilder + recognition services

**Files:** `Models/AppConfiguration.cs`, `Services/PromptBuilder.cs`, `Services/ClaudeRecognitionService.cs`, `Services/GeminiRecognitionService.cs`

#### AppConfiguration.cs

Add after the `GeminiModel` property:

```csharp
/// <summary>
/// Describes the camera's environment, inserted into the AI system prompt.
/// E.g. "a woodland edge with a pond". Must not be blank — default is "a garden".
/// </summary>
public string AiHabitatDescription { get; set; } = "a garden";

/// <summary>
/// Optional species focus hint appended to the AI system prompt.
/// E.g. "UK wildlife, particularly birds and small mammals".
/// Omitted from the prompt when blank.
/// </summary>
public string AiTargetSpeciesHint { get; set; } = "Wildlife, particularly birds";
```

#### PromptBuilder.cs (new file)

```csharp
using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services;

public static class PromptBuilder
{
    private const string JsonFormat =
        "Respond with strict JSON only — no markdown, no code fences. " +
        "Format: {\"detections\":[{\"source_crop_index\":1,\"detected\":true,\"candidates\":[{\"common_name\":\"Red Fox\",\"scientific_name\":\"Vulpes vulpes\",\"confidence\":0.92,\"description\":\"...\"},{\"common_name\":\"Badger\",\"scientific_name\":\"Meles meles\",\"confidence\":0.05,\"description\":\"...\"}]},{\"source_crop_index\":2,\"detected\":true,\"candidates\":[{\"common_name\":\"Robin\",\"scientific_name\":\"Erithacus rubecula\",\"confidence\":0.87,\"description\":\"...\"}]}]} " +
        "Report one entry per crop region that contains an animal. For each detected animal, list up to 3 candidate species in descending confidence order. " +
        "Omit source_crop_index when analysing a single full frame. " +
        "If no animals are visible in any region: {\"detections\":[]}";

    /// <summary>
    /// Builds the full system prompt from user-configured context fields.
    /// </summary>
    public static string Build(AppConfiguration settings)
    {
        var habitat = string.IsNullOrWhiteSpace(settings.AiHabitatDescription)
            ? "a garden"
            : settings.AiHabitatDescription.Trim();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a wildlife identification assistant.");
        sb.AppendLine($"This camera is mounted in {habitat} to observe wild animals and birds.");

        if (!string.IsNullOrWhiteSpace(settings.LocationName))
            sb.AppendLine($"Location: {settings.LocationName.Trim()}.");

        if (!string.IsNullOrWhiteSpace(settings.AiTargetSpeciesHint))
            sb.AppendLine($"Focus on: {settings.AiTargetSpeciesHint.Trim()}.");

        sb.Append(JsonFormat);
        return sb.ToString();
    }

    /// <summary>
    /// Returns only the first 4 context lines for UI preview (no JSON format instructions).
    /// </summary>
    public static string BuildPreview(string habitatDescription, string locationName, string speciesHint)
    {
        var habitat = string.IsNullOrWhiteSpace(habitatDescription)
            ? "a garden"
            : habitatDescription.Trim();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a wildlife identification assistant.");
        sb.AppendLine($"This camera is mounted in {habitat} to observe wild animals and birds.");

        if (!string.IsNullOrWhiteSpace(locationName))
            sb.AppendLine($"Location: {locationName.Trim()}.");

        if (!string.IsNullOrWhiteSpace(speciesHint))
            sb.Append($"Focus on: {speciesHint.Trim()}.");

        return sb.ToString().TrimEnd();
    }
}
```

#### ClaudeRecognitionService.cs

Remove the `const string SystemPrompt` field entirely. In `RecognizeAsync`, replace the `System` parameter line:

```csharp
// Before:
System = new List<SystemMessage> { new(SystemPrompt) },

// After:
System = new List<SystemMessage> { new(PromptBuilder.Build(_settings.CurrentSettings)) },
```

#### GeminiRecognitionService.cs

Remove the `const string SystemPrompt` field entirely. In `RecognizeAsync`, replace the `systemInstruction` argument:

```csharp
// Before:
systemInstruction: new Content(new TextData { Text = SystemPrompt }));

// After:
systemInstruction: new Content(new TextData { Text = PromptBuilder.Build(_settings.CurrentSettings) }));
```

**Verify:** Project builds with no errors. Add a temporary `Console.WriteLine(PromptBuilder.Build(new AppConfiguration()))` in any test context (or just confirm via code review) that the output starts with "You are a wildlife identification assistant." and includes the default habitat/species lines.

---

### Task 2: DetectionSettingsViewModel + DetectionSettingsWindow UI

**Files:** `ViewModels/DetectionSettingsViewModel.cs`, `Views/DetectionSettingsWindow.xaml`
**Depends on:** Task 1

#### DetectionSettingsViewModel.cs

Add two new observable properties alongside the existing AI properties:

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(PromptPreview))]
private string _aiHabitatDescription = "a garden";

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(PromptPreview))]
private string _aiTargetSpeciesHint = "Wildlife, particularly birds";
```

Add computed preview property:

```csharp
public string PromptPreview =>
    PromptBuilder.BuildPreview(AiHabitatDescription, _settings.CurrentSettings.LocationName, AiTargetSpeciesHint);
```

In the constructor where other properties are loaded from `_settings.CurrentSettings`, add:

```csharp
AiHabitatDescription = s.AiHabitatDescription;
AiTargetSpeciesHint  = s.AiTargetSpeciesHint;
```

In `AutoSave()`, add:

```csharp
s.AiHabitatDescription = string.IsNullOrWhiteSpace(AiHabitatDescription) ? "a garden" : AiHabitatDescription;
s.AiTargetSpeciesHint  = AiTargetSpeciesHint;
```

Also add `OnAiHabitatDescriptionChanged` and `OnAiTargetSpeciesHintChanged` partial methods that call `AutoSave()` and raise `PromptPreview` change (the `[NotifyPropertyChangedFor]` attribute handles the latter):

```csharp
partial void OnAiHabitatDescriptionChanged(string value) => AutoSave();
partial void OnAiTargetSpeciesHintChanged(string value)  => AutoSave();
```

Note: `PromptPreview` also depends on `LocationName` from settings (not a VM property). To keep the preview in sync when settings are loaded, add a call to `OnPropertyChanged(nameof(PromptPreview))` at the end of the constructor after all fields are loaded.

#### DetectionSettingsWindow.xaml

Inside the existing AI Recognition section, add a new "AI Prompt" sub-section **above** the provider ComboBox row. Insert after the AI section header/separator:

```xaml
<!-- AI Prompt context -->
<TextBlock Text="Camera Context" Style="{StaticResource SectionHeader}" Margin="0,8,0,0"/>
<Separator Background="#DDDDDD" Margin="0,0,0,8"/>

<TextBlock Text="Habitat description" Style="{StaticResource Label}"/>
<TextBox Text="{Binding AiHabitatDescription, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource Field}"
         ToolTip="Describes the camera's environment, e.g. 'a woodland edge with a pond'"/>
<TextBlock Text="Replaces 'a garden' in the prompt. Must not be blank." Style="{StaticResource Hint}"/>

<TextBlock Text="Species focus hint" Style="{StaticResource Label}" Margin="0,6,0,0"/>
<TextBox Text="{Binding AiTargetSpeciesHint, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource Field}"
         ToolTip="Optional. E.g. 'UK wildlife, particularly birds and small mammals'. Leave blank to omit."/>
<TextBlock Text="Leave blank to omit this line from the prompt." Style="{StaticResource Hint}"/>

<!-- Prompt preview -->
<TextBlock Text="Prompt preview" Style="{StaticResource Label}" Margin="0,10,0,2"/>
<Border Background="#F0F0E8" BorderBrush="#CCCCCC" BorderThickness="1"
        CornerRadius="3" Padding="8,6">
    <TextBlock Text="{Binding PromptPreview}"
               FontFamily="Consolas" FontSize="11"
               Foreground="#333333"
               TextWrapping="Wrap"
               LineHeight="18"/>
</Border>
<TextBlock Text="First lines of the assembled prompt sent to AI. JSON format instructions follow (not shown)."
           Style="{StaticResource Hint}" Margin="0,2,0,8"/>
```

**Verify:** Open Detection Settings. AI section shows "Camera Context" sub-heading with two text fields pre-populated with defaults. The preview box below shows 2–4 lines depending on whether Location and Species hint are set. Editing the habitat field live-updates the preview. Clearing the species hint field removes that line from the preview. Restarting the app retains the saved values.
