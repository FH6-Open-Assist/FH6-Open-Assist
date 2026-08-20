using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using FH6OpenAssist.Core;

namespace FH6OpenAssist.Vision;

public enum CrAttemptGroundTruth
{
    Valid,
    Invalid
}

public sealed record CrFarmAttempt(
    string Id,
    string? PendingDirectory,
    IReadOnlyList<string> FramePaths,
    CrPositionPrediction Prediction);

public sealed class CrFarmSampleCollector(
    AutomationSettings settings,
    AutomationLogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public CrFarmAttempt BeginAttempt(
        IReadOnlyList<Bitmap> frames,
        CrPositionPrediction prediction)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}"[..31];
        if (!settings.CrFarm.CollectSamples)
        {
            return new CrFarmAttempt(id, null, [], prediction);
        }

        var pendingDirectory = Path.Combine(settings.CrFarmDatasetPath, "Pending", id);
        Directory.CreateDirectory(pendingDirectory);
        var framePaths = new List<string>(frames.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            var path = Path.Combine(pendingDirectory, $"{id}__frame{index + 1:00}.jpg");
            SaveJpeg(frames[index], path);
            framePaths.Add(path);
        }

        var attempt = new CrFarmAttempt(id, pendingDirectory, framePaths, prediction);
        WriteMetadata(attempt, groundTruth: null, outcome: "Pending", menu: null);
        logger.State(
            "CR Farm",
            "AmostraPendente",
            $"Tentativa {id}: {framePaths.Count} frame(s) salvos em Pending.");
        return attempt;
    }

    public IReadOnlyList<string> CompleteAttempt(
        CrFarmAttempt attempt,
        CrAttemptGroundTruth groundTruth,
        string outcome,
        GameContextKind menu)
    {
        if (attempt.PendingDirectory is null)
        {
            return [];
        }

        var className = groundTruth.ToString();
        var targetDirectory = Path.Combine(settings.CrFarmDatasetPath, "Dataset", className);
        Directory.CreateDirectory(targetDirectory);
        var moved = new List<string>(attempt.FramePaths.Count);
        foreach (var source in attempt.FramePaths)
        {
            if (!File.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(targetDirectory, Path.GetFileName(source));
            File.Move(source, target, overwrite: true);
            moved.Add(target);
        }

        WriteMetadata(
            attempt with { FramePaths = moved },
            groundTruth,
            outcome,
            menu,
            Path.Combine(targetDirectory, $"{attempt.Id}.json"));
        TryDeleteDirectory(attempt.PendingDirectory);
        TrimClassDirectory(targetDirectory);
        return moved;
    }

    public void KeepPending(
        CrFarmAttempt attempt,
        string outcome,
        GameContextKind? menu = null)
    {
        if (attempt.PendingDirectory is null)
        {
            return;
        }

        WriteMetadata(attempt, groundTruth: null, outcome, menu);
    }

    private void WriteMetadata(
        CrFarmAttempt attempt,
        CrAttemptGroundTruth? groundTruth,
        string outcome,
        GameContextKind? menu,
        string? path = null)
    {
        if (attempt.PendingDirectory is null && path is null)
        {
            return;
        }

        path ??= Path.Combine(attempt.PendingDirectory!, "attempt.json");
        var payload = new
        {
            attempt = attempt.Id,
            capturedAt = DateTimeOffset.Now,
            prediction = attempt.Prediction.Label.ToString(),
            validProbability = attempt.Prediction.ValidProbability,
            minimumValidProbability = attempt.Prediction.MinimumValidProbability,
            maximumValidProbability = attempt.Prediction.MaximumValidProbability,
            frames = attempt.FramePaths.Select(Path.GetFileName).ToArray(),
            groundTruth = groundTruth?.ToString(),
            outcome,
            menu = menu?.ToString()
        };
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void TrimClassDirectory(string directory)
    {
        var maximum = Math.Max(30, settings.CrFarm.MaximumSamplesPerClass);
        var excess = Directory
            .EnumerateFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .Take(Math.Max(0, Directory.EnumerateFiles(directory, "*.jpg").Count() - maximum))
            .ToArray();
        foreach (var file in excess)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                logger.Warn($"Não foi possível remover a amostra antiga '{file.Name}'.");
            }
            catch (UnauthorizedAccessException)
            {
                logger.Warn($"Sem permissão para remover a amostra antiga '{file.Name}'.");
            }
        }
    }

    private static void SaveJpeg(Bitmap bitmap, string path)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
        bitmap.Save(path, encoder, parameters);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A amostra já foi movida; a pasta vazia pode ser limpa depois.
        }
        catch (UnauthorizedAccessException)
        {
            // A coleta não deve derrubar o BOT por uma limpeza cosmética.
        }
    }
}
