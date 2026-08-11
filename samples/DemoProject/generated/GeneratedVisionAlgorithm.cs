using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;

public sealed class GeneratedVisionAlgorithm
{
    public Mat Process(string projectRoot)
    {
        // 加载图像 (load)
        using var mat_load = new Mat();
        CvInvoke.Imdecode(File.ReadAllBytes(ProjectPath(projectRoot, "images/input/input.png")), ImreadModes.Unchanged, mat_load);
        if (mat_load.IsEmpty) throw new InvalidDataException("Could not decode input image.");

        // 灰度化 (gray)
        using var mat_gray = new Mat();
        if (mat_load.NumberOfChannels == 1) mat_load.CopyTo(mat_gray);
        else CvInvoke.CvtColor(mat_load, mat_gray, mat_load.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);

        // 二值化 (threshold)
        using var mat_threshold = new Mat();
        CvInvoke.Threshold(mat_gray, mat_threshold, 128, 255, ThresholdType.Binary);

        // 保存图像 (save)
        var path_save = ProjectPath(projectRoot, "images/output/result.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path_save)!);
        File.WriteAllBytes(path_save, CvInvoke.Imencode(Path.GetExtension(path_save), mat_threshold, Array.Empty<KeyValuePair<ImwriteFlags, int>>()));
        using var mat_save = mat_threshold.Clone();

        return mat_save.Clone();
    }

    private static string ProjectPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException("Expected a project-relative path.");
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path escapes project root.");
        return path;
    }
}
