using System.Text.Json;

namespace ModelExplorer.Core;

/// <summary>
/// What the app remembers between runs: where its window was, which panels were
/// open, how models are shown and lit. Stored as <c>settings.json</c> beside the index.
/// </summary>
/// <remarks>
/// Read and written with the JSON reader and writer directly rather than the
/// serializer: the file is read before the window exists, this costs microseconds,
/// and it needs no reflection under Native AOT. Unknown or damaged entries fall
/// back to their defaults one by one.
/// </remarks>
public sealed class Settings
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelExplorer", "settings.json");

    // Window: the restored window's position and size in screen pixels; zero size means never saved.
    public int WindowX, WindowY, WindowWidth, WindowHeight;
    public bool Maximized;

    public bool ShowLibrary = true, ShowModels = true;
    public bool GridView = true;
    public int ThumbnailSize = 144;
    public int Sort;

    public bool SyncCameras = true, AlignCenters, KeepView;
    public string CompareMode = "Separate";
    public float Tolerance = 0.1f;

    public bool ShowBoundingBox, ShowBuildPlate;
    public string BuildPlate = string.Empty;

    public string Lighting = string.Empty, Shading = string.Empty, ModelColor = string.Empty;
    public bool AmbientEnabled = true, MainLight = true, FillLight = true, RimLight = true, RotateLight;
    public float AmbientIntensity = 0.42f, RotationSpeed = 35;

    /// <summary>Loads the settings; a missing or damaged file gives the defaults.</summary>
    public static Settings Load(string path)
    {
        var settings = new Settings();
        byte[] json;
        try
        {
            json = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return settings;
        }

        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return settings;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read())
                {
                    break;
                }

                settings.Read(name, ref reader);
            }
        }
        catch (JsonException)
        {
            // What was read so far stands; the rest keeps its defaults.
        }

        return settings;
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber(nameof(WindowX), WindowX);
                writer.WriteNumber(nameof(WindowY), WindowY);
                writer.WriteNumber(nameof(WindowWidth), WindowWidth);
                writer.WriteNumber(nameof(WindowHeight), WindowHeight);
                writer.WriteBoolean(nameof(Maximized), Maximized);
                writer.WriteBoolean(nameof(ShowLibrary), ShowLibrary);
                writer.WriteBoolean(nameof(ShowModels), ShowModels);
                writer.WriteBoolean(nameof(GridView), GridView);
                writer.WriteNumber(nameof(ThumbnailSize), ThumbnailSize);
                writer.WriteNumber(nameof(Sort), Sort);
                writer.WriteBoolean(nameof(SyncCameras), SyncCameras);
                writer.WriteBoolean(nameof(AlignCenters), AlignCenters);
                writer.WriteBoolean(nameof(KeepView), KeepView);
                writer.WriteString(nameof(CompareMode), CompareMode);
                writer.WriteNumber(nameof(Tolerance), Tolerance);
                writer.WriteBoolean(nameof(ShowBoundingBox), ShowBoundingBox);
                writer.WriteBoolean(nameof(ShowBuildPlate), ShowBuildPlate);
                writer.WriteString(nameof(BuildPlate), BuildPlate);
                writer.WriteString(nameof(Lighting), Lighting);
                writer.WriteString(nameof(Shading), Shading);
                writer.WriteString(nameof(ModelColor), ModelColor);
                writer.WriteBoolean(nameof(AmbientEnabled), AmbientEnabled);
                writer.WriteNumber(nameof(AmbientIntensity), AmbientIntensity);
                writer.WriteBoolean(nameof(MainLight), MainLight);
                writer.WriteBoolean(nameof(FillLight), FillLight);
                writer.WriteBoolean(nameof(RimLight), RimLight);
                writer.WriteBoolean(nameof(RotateLight), RotateLight);
                writer.WriteNumber(nameof(RotationSpeed), RotationSpeed);
                writer.WriteEndObject();
            }

            // Through a temporary file, so a crash mid-write never leaves a torn file.
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembering the window position is no reason to fail closing it.
        }
    }

    private void Read(string? name, ref Utf8JsonReader reader)
    {
        switch (name)
        {
            case nameof(WindowX): WindowX = Int(ref reader, WindowX); break;
            case nameof(WindowY): WindowY = Int(ref reader, WindowY); break;
            case nameof(WindowWidth): WindowWidth = Int(ref reader, WindowWidth); break;
            case nameof(WindowHeight): WindowHeight = Int(ref reader, WindowHeight); break;
            case nameof(Maximized): Maximized = Bool(ref reader, Maximized); break;
            case nameof(ShowLibrary): ShowLibrary = Bool(ref reader, ShowLibrary); break;
            case nameof(ShowModels): ShowModels = Bool(ref reader, ShowModels); break;
            case nameof(GridView): GridView = Bool(ref reader, GridView); break;
            case nameof(ThumbnailSize): ThumbnailSize = Int(ref reader, ThumbnailSize); break;
            case nameof(Sort): Sort = Int(ref reader, Sort); break;
            case nameof(SyncCameras): SyncCameras = Bool(ref reader, SyncCameras); break;
            case nameof(AlignCenters): AlignCenters = Bool(ref reader, AlignCenters); break;
            case nameof(KeepView): KeepView = Bool(ref reader, KeepView); break;
            case nameof(CompareMode): CompareMode = String(ref reader, CompareMode); break;
            case nameof(Tolerance): Tolerance = Float(ref reader, Tolerance); break;
            case nameof(ShowBoundingBox): ShowBoundingBox = Bool(ref reader, ShowBoundingBox); break;
            case nameof(ShowBuildPlate): ShowBuildPlate = Bool(ref reader, ShowBuildPlate); break;
            case nameof(BuildPlate): BuildPlate = String(ref reader, BuildPlate); break;
            case nameof(Lighting): Lighting = String(ref reader, Lighting); break;
            case nameof(Shading): Shading = String(ref reader, Shading); break;
            case nameof(ModelColor): ModelColor = String(ref reader, ModelColor); break;
            case nameof(AmbientEnabled): AmbientEnabled = Bool(ref reader, AmbientEnabled); break;
            case nameof(AmbientIntensity): AmbientIntensity = Float(ref reader, AmbientIntensity); break;
            case nameof(MainLight): MainLight = Bool(ref reader, MainLight); break;
            case nameof(FillLight): FillLight = Bool(ref reader, FillLight); break;
            case nameof(RimLight): RimLight = Bool(ref reader, RimLight); break;
            case nameof(RotateLight): RotateLight = Bool(ref reader, RotateLight); break;
            case nameof(RotationSpeed): RotationSpeed = Float(ref reader, RotationSpeed); break;
            default: reader.Skip(); break;
        }
    }

    private static int Int(ref Utf8JsonReader reader, int fallback) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) ? value : fallback;

    private static float Float(ref Utf8JsonReader reader, float fallback) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetSingle(out var value) && float.IsFinite(value) ? value : fallback;

    private static bool Bool(ref Utf8JsonReader reader, bool fallback) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        _ => fallback,
    };

    private static string String(ref Utf8JsonReader reader, string fallback) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() ?? fallback : fallback;
}
