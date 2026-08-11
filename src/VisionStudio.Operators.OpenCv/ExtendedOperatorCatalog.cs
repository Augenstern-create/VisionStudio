using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using VisionStudio.Core;

namespace VisionStudio.Operators.OpenCv;

public sealed record VisionContours(IReadOnlyList<Point[]> Items);
public sealed record VisionPoint(double X, double Y);
public sealed record VisionLine(double X1, double Y1, double X2, double Y2);
public sealed record VisionCircle(double X, double Y, double Radius);

public static class ExtendedOperatorCatalog
{
    private static readonly IReadOnlyList<ParameterDefinition> None = [];
    private static PortDefinition I(string name = "Image", string title = "输入图像", bool required = true, NodeValueType type = NodeValueType.Mat) => new(name, type, required, title);
    private static PortDefinition O(string name = "Image", string title = "结果图像", NodeValueType type = NodeValueType.Mat) => new(name, type, true, title);
    private static ParameterDefinition N(string name, string title, double value, double? min = null, double? max = null, string? text = null) => new(name, title, JsonValueKind.Number, value, min, max, text);
    private static ParameterDefinition Z(string name, string title, int value, double? min = null, double? max = null, string? text = null) => new(name, title, JsonValueKind.Number, value, min, max, text);
    private static ParameterDefinition S(string name, string title, string value, string? text = null) => new(name, title, JsonValueKind.String, value, Description: text);
    private static ParameterDefinition B(string name, string title, bool value, string? text = null) => new(name, title, JsonValueKind.True, value, Description: text);

    public static void RegisterInto(OperatorRegistry registry)
    {
        void R(string type, string title, string category, IReadOnlyList<PortDefinition> inputs, IReadOnlyList<PortDefinition> outputs, IReadOnlyList<ParameterDefinition>? parameters = null, string? description = null)
        {
            var chineseTitle = ChineseTitle(type, title);
            var englishTitle = EnglishTitle(type);
            registry.Register(new(type, chineseTitle, category, inputs, outputs, parameters ?? None, description ?? $"{chineseTitle}算子。", englishTitle, EnglishDescription(type, englishTitle)), () => new ExtendedVisionOperator(type, outputs));
        }

        R("Input.Camera", "相机", "输入", [], [O()], [Z("index", "相机编号", 0, 0, 32)], "从指定相机采集一帧图像。");
        R("Input.Video", "视频", "输入", [], [O()], [S("path", "视频文件", "images/input/video.mp4"), Z("frame", "帧序号", 0, 0)], "读取项目内视频的指定帧。");
        R("Input.Constant", "常量", "输入", [], [O("Value", "数值", NodeValueType.Double)], [N("value", "常量值", 0)], "输出可连接的数值常量。");

        R("Image.ROI", "感兴趣区域 ROI", "图像", [I()], [O()], [Z("x", "左上 X", 0, 0), Z("y", "左上 Y", 0, 0), Z("width", "宽度", 100, 1), Z("height", "高度", 100, 1)]);
        R("Image.Resize", "调整尺寸", "图像", [I()], [O()], [Z("width", "目标宽度", 640, 1), Z("height", "目标高度", 480, 1), S("interpolation", "插值", "Linear", "Nearest、Linear、Cubic、Area 或 Lanczos4")]);
        R("Image.Rotate", "旋转", "图像", [I()], [O()], [N("angle", "角度", 90), N("scale", "缩放", 1, 0.001)]);
        R("Image.Flip", "翻转", "图像", [I()], [O()], [S("direction", "方向", "Horizontal", "Horizontal、Vertical 或 Both")]);
        R("Image.BgrToHsv", "BGR → HSV", "图像", [I()], [O()]);
        R("Image.BgrToLab", "BGR → Lab", "图像", [I()], [O()]);
        R("Image.ColorConvert", "颜色转换", "图像", [I()], [O()], [S("mode", "转换模式", "Bgr2Gray", "例如 Bgr2Gray、Bgr2Hsv、Bgr2Lab、Gray2Bgr")]);
        R("Image.Split", "通道拆分 Split", "图像", [I()], [O("Channel0", "通道 0"), O("Channel1", "通道 1"), O("Channel2", "通道 2"), O("Channel3", "通道 3")], None, "把图像通道拆成四个独立可连接输出；不足的通道复制最后一个有效通道。");

        R("Filter.Blur", "均值滤波 Blur", "滤波", [I()], [O()], [Z("kernelSize", "核大小", 5, 1, 99)]);
        R("Filter.Box", "方框滤波 BoxFilter", "滤波", [I()], [O()], [Z("kernelSize", "核大小", 5, 1, 99), B("normalize", "归一化", true)]);
        R("Filter.Median", "中值滤波 MedianBlur", "滤波", [I()], [O()], [Z("kernelSize", "核大小", 5, 1, 99)]);
        R("Filter.Bilateral", "双边滤波 Bilateral", "滤波", [I()], [O()], [Z("diameter", "邻域直径", 9, 1, 99), N("sigmaColor", "颜色 Sigma", 75, 0), N("sigmaSpace", "空间 Sigma", 75, 0)]);

        R("Enhance.Normalize", "归一化 Normalize", "增强", [I()], [O()], [N("alpha", "下限", 0), N("beta", "上限", 255)]);
        R("Enhance.CLAHE", "局部直方图均衡 CLAHE", "增强", [I()], [O()], [N("clipLimit", "裁剪限制", 2, 0.01), Z("tileSize", "网格大小", 8, 1, 64)]);
        R("Enhance.Histogram", "直方图", "增强", [I()], [O(), O("Histogram", "直方图数据", NodeValueType.Collection)], [Z("bins", "分箱数", 256, 2, 1024)]);
        R("Enhance.EqualizeHist", "直方图均衡 EqualizeHist", "增强", [I()], [O()]);
        R("Enhance.ConvertScaleAbs", "线性缩放 ConvertScaleAbs", "增强", [I()], [O()], [N("alpha", "增益", 1), N("beta", "偏移", 0)]);

        R("Threshold.Otsu", "Otsu 自动阈值", "阈值", [I()], [O(), O("Threshold", "自动阈值", NodeValueType.Double)]);
        R("Threshold.Triangle", "Triangle 自动阈值", "阈值", [I()], [O(), O("Threshold", "自动阈值", NodeValueType.Double)]);
        R("Threshold.Adaptive", "自适应阈值 AdaptiveThreshold", "阈值", [I()], [O()], [Z("blockSize", "邻域大小", 11, 3, 255), N("c", "常数 C", 2), S("method", "方法", "Gaussian")]);
        R("Threshold.InRange", "范围阈值 InRange", "阈值", [I()], [O()], [N("lower0", "下限 0", 0, 0, 255), N("lower1", "下限 1", 0, 0, 255), N("lower2", "下限 2", 0, 0, 255), N("upper0", "上限 0", 255, 0, 255), N("upper1", "上限 1", 255, 0, 255), N("upper2", "上限 2", 255, 0, 255)]);
        R("Segmentation.DistanceTransform", "距离变换 DistanceTransform", "分割", [I()], [O()], [S("distance", "距离类型", "L2"), Z("maskSize", "掩膜大小", 3, 3, 5)]);
        R("Segmentation.Watershed", "分水岭 Watershed", "分割", [I(), I("Markers", "标记图")], [O(), O("Labels", "标签图", NodeValueType.Mat)], None, "使用彩色输入图和标记图执行分水岭分割。");

        foreach (var item in new[] { ("Erode", "腐蚀"), ("Dilate", "膨胀"), ("Open", "开运算"), ("Close", "闭运算"), ("Gradient", "形态学梯度"), ("TopHat", "顶帽"), ("BlackHat", "黑帽") })
            R("Morphology." + item.Item1, item.Item2, "形态学", [I()], [O()], [Z("kernelSize", "核大小", 3, 1, 99), Z("iterations", "迭代次数", 1, 1, 100)]);

        R("Edge.Canny", "Canny 边缘", "边缘", [I()], [O()], [N("threshold1", "低阈值", 50, 0), N("threshold2", "高阈值", 150, 0), Z("aperture", "孔径", 3, 3, 7)]);
        foreach (var item in new[] { ("Sobel", "Sobel"), ("Scharr", "Scharr"), ("Laplacian", "Laplacian") })
            R("Edge." + item.Item1, item.Item2 + " 边缘", "边缘", [I()], [O()], [Z("dx", "X 阶数", item.Item1 == "Laplacian" ? 0 : 1, 0, 2), Z("dy", "Y 阶数", item.Item1 == "Laplacian" ? 0 : 0, 0, 2), Z("kernelSize", "核大小", 3, 1, 31)]);

        R("Blob.Contours", "查找轮廓 FindContours", "Blob", [I()], [O(), O("Contours", "轮廓集合", NodeValueType.Contours), O("Count", "轮廓数量", NodeValueType.Integer)], [S("mode", "检索模式", "External")], "查找轮廓，并把轮廓绘制在输入图像上。");
        R("Blob.ConnectedComponents", "连通域 ConnectedComponents", "Blob", [I()], [O(), O("Labels", "标签图", NodeValueType.Mat), O("Count", "区域数量", NodeValueType.Integer)]);
        R("Blob.Filter", "Blob 筛选", "Blob", [I(), I("Contours", "轮廓集合", true, NodeValueType.Contours)], [O(), O("Contours", "筛选后轮廓", NodeValueType.Contours), O("Count", "数量", NodeValueType.Integer)], [N("minArea", "最小面积", 10, 0), N("maxArea", "最大面积", 1_000_000, 0)]);

        var contourInput = new[] { I(), I("Contours", "轮廓集合", true, NodeValueType.Contours) };
        foreach (var item in new[] { ("Area", "面积", NodeValueType.Double), ("Perimeter", "周长 ArcLength", NodeValueType.Double), ("Center", "中心", NodeValueType.Point), ("BoundingRect", "外接矩形 BoundingRect", NodeValueType.Rectangle), ("MinAreaRect", "最小外接矩形 MinAreaRect", NodeValueType.Rectangle), ("FitEllipse", "拟合椭圆 FitEllipse", NodeValueType.Any), ("MinCircle", "最小包围圆 MinEnclosingCircle", NodeValueType.Circle), ("ConvexHull", "凸包 ConvexHull", NodeValueType.Contours), ("ConvexityDefects", "凸缺陷 ConvexityDefects", NodeValueType.Collection), ("ApproxPolygon", "多边形逼近 ApproxPolyDP", NodeValueType.Contours), ("Moments", "矩 Moments", NodeValueType.Collection), ("Circularity", "圆度 Circularity", NodeValueType.Double), ("AspectRatio", "长宽比 Aspect Ratio", NodeValueType.Double), ("Rectangularity", "矩形度 Rectangularity", NodeValueType.Double) })
            R("Contour." + item.Item1, item.Item2, "轮廓", contourInput, [O(), O("Result", "结果", item.Item3)], item.Item1 == "ApproxPolygon" ? [N("epsilon", "逼近精度", 3, 0)] : None, "分析第一个轮廓，并在输入图像上绘制结果及文字。");

        R("LineCircle.HoughLines", "标准霍夫直线 HoughLines", "直线/圆", [I()], [O(), O("Lines", "直线集合", NodeValueType.Collection)]);
        R("LineCircle.HoughLinesP", "概率霍夫直线 HoughLinesP", "直线/圆", [I()], [O(), O("Lines", "线段集合", NodeValueType.Collection)], [N("threshold", "投票阈值", 50, 1), N("minLength", "最短长度", 30, 0), N("maxGap", "最大间隙", 10, 0)]);
        R("LineCircle.HoughCircles", "霍夫圆 HoughCircles", "直线/圆", [I()], [O(), O("Circles", "圆集合", NodeValueType.Collection)], [N("dp", "累加器比例", 1, 0.1), N("minDistance", "最小圆距", 20, 1), N("param1", "边缘阈值", 100, 1), N("param2", "圆心阈值", 30, 1)]);
        R("LineCircle.FitLine", "拟合直线 FitLine", "直线/圆", contourInput, [O(), O("Line", "拟合直线", NodeValueType.Line)]);
        R("LineCircle.FitCircle", "拟合圆 FitCircle", "直线/圆", contourInput, [O(), O("Circle", "拟合圆", NodeValueType.Circle)]);
        R("LineCircle.LineAngle", "直线角度", "直线/圆", [I(), I("Line", "直线", true, NodeValueType.Line)], [O(), O("Angle", "角度", NodeValueType.Double)]);

        R("Geometry.Point", "点", "几何", [], [O("Point", "点", NodeValueType.Point)], [N("x", "X", 0), N("y", "Y", 0)]);
        R("Geometry.Line", "直线", "几何", [], [O("Line", "直线", NodeValueType.Line)], [N("x1", "起点 X", 0), N("y1", "起点 Y", 0), N("x2", "终点 X", 100), N("y2", "终点 Y", 0)]);
        R("Geometry.Circle", "圆", "几何", [], [O("Circle", "圆", NodeValueType.Circle)], [N("x", "圆心 X", 0), N("y", "圆心 Y", 0), N("radius", "半径", 10, 0)]);
        R("Geometry.Distance", "点间距离", "几何", [I("PointA", "点 A", true, NodeValueType.Point), I("PointB", "点 B", true, NodeValueType.Point)], [O("Distance", "距离", NodeValueType.Double)]);
        R("Geometry.Angle", "两直线夹角", "几何", [I("LineA", "直线 A", true, NodeValueType.Line), I("LineB", "直线 B", true, NodeValueType.Line)], [O("Angle", "角度", NodeValueType.Double)]);
        R("Geometry.Intersection", "直线交点", "几何", [I("LineA", "直线 A", true, NodeValueType.Line), I("LineB", "直线 B", true, NodeValueType.Line)], [O("Point", "交点", NodeValueType.Point), O("Parallel", "是否平行", NodeValueType.Boolean)]);

        R("Locate.TemplateMatch", "模板匹配 TemplateMatch", "定位", [I("Image", "搜索图像"), I("Template", "模板图像")], [O(), O("Score", "匹配分数", NodeValueType.Double), O("Point", "最佳位置", NodeValueType.Point)], [S("method", "方法", "CcoeffNormed")]);
        R("Locate.TemplateFindBest", "模板最佳匹配", "定位", [I("Image", "搜索图像"), I("Template", "模板图像")], [O(), O("Score", "最佳分数", NodeValueType.Double), O("Point", "最佳位置", NodeValueType.Point)]);
        R("Locate.MultiTemplate", "多模板匹配", "定位", [I("Image", "搜索图像"), I("Template", "模板图像")], [O(), O("Points", "匹配位置", NodeValueType.Points), O("Count", "数量", NodeValueType.Integer)], [N("threshold", "分数阈值", .8, -1, 1), Z("maxCount", "最多数量", 20, 1, 1000)]);
        foreach (var item in new[] { ("ORB", "ORB 特征"), ("SIFT", "SIFT 特征"), ("BFMatcher", "BFMatcher"), ("FLANN", "FLANN Matcher"), ("FeatureMatch", "特征匹配"), ("Homography", "单应性 FindHomography"), ("PhaseCorrelation", "相位相关 PhaseCorrelate") })
            R("Locate." + item.Item1, item.Item2, "定位", [I("ImageA", "图像 A"), I("ImageB", "图像 B")], [O(), O("Result", "定位结果", NodeValueType.Any)], None, "计算两幅图像的定位关系，并在图像 A 上显示结果。");

        foreach (var item in new[] { ("WarpPerspective", "透视变换 WarpPerspective"), ("WarpAffine", "仿射变换 WarpAffine"), ("GetPerspective", "求透视矩阵 GetPerspectiveTransform"), ("GetAffine", "求仿射矩阵 GetAffineTransform"), ("Remap", "重映射 Remap") })
            R("Transform." + item.Item1, item.Item2, "变换", [I()], [O(), O("Matrix", "变换矩阵", NodeValueType.Matrix)], [S("points", "点坐标", "", "格式：x1,y1;x2,y2;…")]);

        foreach (var item in new[] { ("Add", "相加 Add"), ("Subtract", "相减 Subtract"), ("AbsDiff", "绝对差 AbsDiff"), ("And", "按位 AND"), ("Or", "按位 OR"), ("Xor", "按位 XOR") })
            R("Arithmetic." + item.Item1, item.Item2, "图像运算", [I("ImageA", "图像 A"), I("ImageB", "图像 B")], [O()]);
        R("Arithmetic.Not", "按位 NOT", "图像运算", [I()], [O()]);
        R("Arithmetic.MaskApply", "应用掩膜 Mask Apply", "图像运算", [I(), I("Mask", "掩膜")], [O()]);

        foreach (var item in new[] { ("Mean", "均值", NodeValueType.Double), ("StdDev", "标准差", NodeValueType.Double), ("MinMax", "最小最大值", NodeValueType.String), ("CountNonZero", "非零像素数", NodeValueType.Integer), ("PixelRatio", "像素比例", NodeValueType.Double), ("Integral", "积分图 Integral Image", NodeValueType.Mat), ("DFT", "频谱 FFT / DFT", NodeValueType.Mat), ("IDFT", "逆频谱 IDFT", NodeValueType.Mat) })
            R("Statistics." + item.Item1, item.Item2, "统计", [I()], [O(), O("Result", "统计结果", item.Item3)]);

        foreach (var item in new[] { ("CornerHarris", "Harris 角点"), ("GoodFeatures", "优质角点 GoodFeaturesToTrack"), ("SubPix", "亚像素角点 SubPix Corner"), ("LineFit", "直线拟合") })
            R("Measure." + item.Item1, item.Item2, "测量", [I()], [O(), O("Result", "测量结果", NodeValueType.Any)]);
        foreach (var item in new[] { ("PointDistance", "点间距离"), ("PointToLine", "点到直线距离"), ("ParallelLines", "平行线距离"), ("LineAngle", "两直线夹角"), ("Width", "宽度"), ("Angle", "角度"), ("Area", "面积"), ("Circularity", "圆度") })
            R("Measure." + item.Item1, item.Item2, "测量", [I()], [O(), O("Result", "测量值", NodeValueType.Double)], [S("geometry", "几何数据", "")]);
        R("Measure.Caliper", "卡尺 Caliper", "测量", [I()], [O(), O("Result", "边缘位置", NodeValueType.Points)], [S("line", "测量线", "0,0;100,0")]);
        R("Measure.CircleCaliper", "圆卡尺 Circle Caliper", "测量", [I()], [O(), O("Result", "圆结果", NodeValueType.Circle)], [S("circle", "初始圆", "100,100,50")]);

        foreach (var item in new[] { ("CameraCalibration", "相机标定 CalibrateCamera"), ("FindChessboard", "查找棋盘角点"), ("Undistort", "畸变校正 Undistort"), ("Perspective", "透视标定"), ("PixelToWorld", "像素转物理坐标"), ("PixelDistance", "像素距离"), ("PhysicalDistance", "物理距离"), ("PointTransform", "点坐标变换") })
            R("Calibration." + item.Item1, item.Item2, "标定", [I()], [O(), O("Result", "标定结果", NodeValueType.Any)], [S("calibration", "标定参数", "")]);

        R("Logic.Compare", "比较 Compare", "逻辑", [I("A", "值 A", true, NodeValueType.Any), I("B", "值 B", true, NodeValueType.Any)], [O("Result", "比较结果", NodeValueType.Boolean)], [S("operator", "比较符", ">")]);
        foreach (var item in new[] { ("And", "逻辑 AND"), ("Or", "逻辑 OR") }) R("Logic." + item.Item1, item.Item2, "逻辑", [I("A", "布尔 A", true, NodeValueType.Boolean), I("B", "布尔 B", true, NodeValueType.Boolean)], [O("Result", "结果", NodeValueType.Boolean)]);
        R("Logic.Not", "逻辑 NOT", "逻辑", [I("Value", "布尔值", true, NodeValueType.Boolean)], [O("Result", "结果", NodeValueType.Boolean)]);
        R("Logic.Switch", "条件切换 Switch", "逻辑", [I("Condition", "条件", true, NodeValueType.Boolean), I("True", "真值", true, NodeValueType.Any), I("False", "假值", true, NodeValueType.Any)], [O("Result", "结果", NodeValueType.Any)]);
        foreach (var item in new[] { ("Select", "选择"), ("Filter", "筛选"), ("Sort", "排序"), ("Count", "计数") }) R("Collection." + item.Item1, item.Item2, "集合", [I("Items", "集合", true, NodeValueType.Collection)], [O("Result", "结果", item.Item1 == "Count" ? NodeValueType.Integer : NodeValueType.Collection)], [S("expression", "表达式", "")]);

        foreach (var item in new[] { ("Point", "绘制点"), ("Line", "绘制线"), ("Rect", "绘制矩形"), ("Circle", "绘制圆"), ("Contour", "绘制轮廓"), ("Text", "绘制文字") })
            R("Draw." + item.Item1, item.Item2, "显示", [I()], [O()], [S("value", "绘制内容", item.Item1 == "Text" ? "Vision Studio" : "")]);

        foreach (var item in new[] { ("Classification", "图像分类"), ("Detection", "目标检测"), ("Segmentation", "图像分割"), ("OCR", "文字识别 OCR") })
            R("AI." + item.Item1, item.Item2, "AI", [I()], [O(), O("Result", "推理结果", NodeValueType.String)], [S("model", "模型文件", "models/model.onnx"), S("labels", "标签文件", "models/labels.txt")], "使用 ONNX 模型推理；运行前必须把模型放入项目目录并配置输入输出。");
    }

    private static string EnglishTitle(string type) => type[(type.LastIndexOf('.') + 1)..] switch
    {
        "ROI" => "Region of Interest", "BgrToHsv" => "BGR to HSV", "BgrToLab" => "BGR to Lab", "ColorConvert" => "Color Conversion",
        "Split" => "Split Channels", "Blur" => "Mean Blur", "Box" => "Box Filter", "Median" => "Median Blur", "Bilateral" => "Bilateral Filter",
        "CLAHE" => "CLAHE", "EqualizeHist" => "Histogram Equalization", "ConvertScaleAbs" => "Convert Scale Abs",
        "InRange" => "Range Threshold", "DistanceTransform" => "Distance Transform", "Watershed" => "Watershed Segmentation",
        "Erode" => "Erosion", "Dilate" => "Dilation", "Open" when type.StartsWith("Morphology.") => "Morphological Opening",
        "Close" => "Morphological Closing", "Gradient" => "Morphological Gradient", "TopHat" => "Top-Hat Transform", "BlackHat" => "Black-Hat Transform",
        "Contours" => "Find Contours", "ConnectedComponents" => "Connected Components", "Filter" when type.StartsWith("Blob.") => "Blob Filter",
        "BoundingRect" => "Bounding Rectangle", "MinAreaRect" => "Minimum-Area Rectangle", "FitEllipse" => "Fit Ellipse",
        "MinCircle" => "Minimum Enclosing Circle", "ConvexHull" => "Convex Hull", "ConvexityDefects" => "Convexity Defects",
        "ApproxPolygon" => "Polygon Approximation", "Circularity" when type.StartsWith("Contour.") => "Contour Circularity",
        "AspectRatio" => "Contour Aspect Ratio", "Rectangularity" => "Contour Rectangularity",
        "HoughLines" => "Hough Lines", "HoughLinesP" => "Probabilistic Hough Lines", "HoughCircles" => "Hough Circles",
        "FitLine" => "Fit Line", "FitCircle" => "Fit Circle", "LineAngle" when type.StartsWith("LineCircle.") => "Line Angle",
        "TemplateMatch" => "Template Matching", "TemplateFindBest" => "Best Template Match", "MultiTemplate" => "Multiple Template Matching",
        "PhaseCorrelation" => "Phase Correlation", "Homography" => "Find Homography", "FeatureMatch" => "Feature Matching",
        "WarpPerspective" => "Warp Perspective", "WarpAffine" => "Warp Affine", "GetPerspective" => "Get Perspective Transform",
        "GetAffine" => "Get Affine Transform", "MaskApply" => "Apply Mask", "MinMax" => "Min-Max Location",
        "CountNonZero" => "Count Non-Zero", "Integral" => "Integral Image", "DFT" => "Discrete Fourier Transform", "IDFT" => "Inverse Discrete Fourier Transform",
        "CornerHarris" => "Harris Corner Detection", "GoodFeatures" => "Good Features to Track", "SubPix" => "Subpixel Corner Refinement",
        "CameraCalibration" => "Camera Calibration", "FindChessboard" => "Find Chessboard Corners", "PixelToWorld" => "Pixel to World",
        "And" when type.StartsWith("Arithmetic.") => "Bitwise AND",
        "Or" when type.StartsWith("Arithmetic.") => "Bitwise OR", "Xor" => "Bitwise XOR", "Not" when type.StartsWith("Arithmetic.") => "Bitwise NOT",
        "Otsu" => "Otsu Threshold", "Triangle" => "Triangle Threshold", "Adaptive" => "Adaptive Threshold",
        "Sobel" => "Sobel Gradient", "Scharr" => "Scharr Gradient", "Laplacian" => "Laplacian Edge Detection", "Canny" => "Canny Edge Detection",
        "Moments" => "Contour Moments", "Area" when type.StartsWith("Contour.") => "Contour Area", "Perimeter" => "Contour Perimeter",
        var name => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2")
    };

    private static string EnglishDescription(string type, string title) => type switch
    {
        "Edge.Canny" => "Detects thin binary edges with gradient magnitude, non-maximum suppression, and dual-threshold hysteresis. Use it when a clean edge map is required.",
        "Edge.Sobel" => "Computes a directional first-order image derivative with the Sobel kernel. Select X or Y derivative order to inspect horizontal or vertical intensity changes.",
        "Edge.Scharr" => "Computes a more rotationally accurate 3×3 first-order derivative than Sobel. Use it when small kernels and gradient accuracy matter.",
        "Edge.Laplacian" => "Computes a direction-independent second-order derivative. It emphasizes rapid intensity changes but is more sensitive to noise than first-order gradients.",
        "Contour.Moments" => "Calculates spatial and central moments for the primary contour. Moments support area, centroid, orientation, and shape-feature calculations.",
        _ => $"Runs the {title} operation using the declared typed inputs and outputs. See the parameter descriptions for constraints and tuning guidance."
    };

    private static string ChineseTitle(string type, string title)
    {
        var special = type switch
        {
            "Image.BgrToHsv" => "颜色空间转换（色相饱和度明度）",
            "Image.BgrToLab" => "颜色空间转换（明度色彩）",
            "Threshold.Otsu" => "大津自动阈值",
            "Threshold.Triangle" => "三角自动阈值",
            "Arithmetic.And" => "按位与",
            "Arithmetic.Or" => "按位或",
            "Arithmetic.Xor" => "按位异或",
            "Arithmetic.Not" => "按位取反",
            "Logic.And" => "逻辑与",
            "Logic.Or" => "逻辑或",
            "Logic.Not" => "逻辑非",
            "Locate.ORB" => "方向旋转特征",
            "Locate.SIFT" => "尺度不变特征",
            "Locate.BFMatcher" => "暴力特征匹配",
            "Locate.FLANN" => "快速近邻特征匹配",
            "Edge.Canny" => "坎尼边缘检测",
            "Edge.Sobel" => "索贝尔方向梯度",
            "Edge.Scharr" => "沙尔精确梯度",
            "Edge.Laplacian" => "拉普拉斯边缘检测",
            "Contour.Moments" => "轮廓矩特征",
            _ => null
        };
        if (special is not null) return special;
        var chinese = Regex.Replace(title, "[A-Za-z][A-Za-z0-9]*", "");
        chinese = Regex.Replace(chinese, @"[→/()（）]+", " ");
        chinese = Regex.Replace(chinese, @"\s+", " ").Trim(' ', '-', '—');
        return string.IsNullOrWhiteSpace(chinese) ? "视觉算子" : chinese;
    }
}

internal sealed class ExtendedVisionOperator(string type, IReadOnlyList<PortDefinition> declaredOutputs) : IVisionOperator
{
    public ValueTask ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (type)
        {
            case "Input.Camera": CaptureCamera(context); break;
            case "Input.Video": ReadVideo(context); break;
            case "Input.Constant": context.SetOutput("Value", context.GetParameter("value", 0d)); break;
            case "Image.ROI": Crop(context); break;
            case "Image.Resize": Resize(context); break;
            case "Image.Rotate": Rotate(context); break;
            case "Image.Flip": Flip(context); break;
            case "Image.BgrToHsv": ConvertColor(context, ColorConversion.Bgr2Hsv); break;
            case "Image.BgrToLab": ConvertColor(context, ColorConversion.Bgr2Lab); break;
            case "Image.ColorConvert": ConvertColor(context, ParseEnum(context.GetParameter("mode", "Bgr2Gray"), ColorConversion.Bgr2Gray)); break;
            case "Image.Split": Split(context); break;
            case "Filter.Blur": Blur(context); break;
            case "Filter.Box": Box(context); break;
            case "Filter.Median": Median(context); break;
            case "Filter.Bilateral": Bilateral(context); break;
            case "Enhance.Normalize": Normalize(context); break;
            case "Enhance.CLAHE": Clahe(context); break;
            case "Enhance.Histogram": Histogram(context); break;
            case "Enhance.EqualizeHist": Equalize(context); break;
            case "Enhance.ConvertScaleAbs": ConvertScale(context); break;
            case "Threshold.Otsu": AutoThreshold(context, ThresholdType.Otsu); break;
            case "Threshold.Triangle": AutoThreshold(context, ThresholdType.Triangle); break;
            case "Threshold.Adaptive": Adaptive(context); break;
            case "Threshold.InRange": InRange(context); break;
            case "Segmentation.DistanceTransform": DistanceTransform(context); break;
            case "Segmentation.Watershed": Watershed(context); break;
            case "Morphology.Erode": Morph(context, MorphOp.Erode); break;
            case "Morphology.Dilate": Morph(context, MorphOp.Dilate); break;
            case "Morphology.Open": Morph(context, MorphOp.Open); break;
            case "Morphology.Close": Morph(context, MorphOp.Close); break;
            case "Morphology.Gradient": Morph(context, MorphOp.Gradient); break;
            case "Morphology.TopHat": Morph(context, MorphOp.Tophat); break;
            case "Morphology.BlackHat": Morph(context, MorphOp.Blackhat); break;
            case "Edge.Canny": Canny(context); break;
            case "Edge.Sobel": Derivative(context, "Sobel"); break;
            case "Edge.Scharr": Derivative(context, "Scharr"); break;
            case "Edge.Laplacian": Derivative(context, "Laplacian"); break;
            case "Blob.Contours": FindContours(context); break;
            case "Blob.Filter": FilterContours(context); break;
            case "Blob.ConnectedComponents": Connected(context); break;
            case var contour when contour.StartsWith("Contour.", StringComparison.Ordinal): AnalyzeContour(context); break;
            case "Locate.TemplateMatch": case "Locate.TemplateFindBest": Template(context, false); break;
            case "Locate.MultiTemplate": Template(context, true); break;
            case "LineCircle.HoughLines": case "LineCircle.HoughLinesP": HoughLines(context); break;
            case "LineCircle.HoughCircles": HoughCircles(context); break;
            case "LineCircle.FitLine": FitLine(context); break;
            case "LineCircle.FitCircle": AnalyzeContour(context); break;
            case "LineCircle.LineAngle": LineAngle(context); break;
            case "Geometry.Point": context.SetOutput("Point", new VisionPoint(context.GetParameter("x", 0d), context.GetParameter("y", 0d))); break;
            case "Geometry.Line": context.SetOutput("Line", new VisionLine(context.GetParameter("x1", 0d), context.GetParameter("y1", 0d), context.GetParameter("x2", 100d), context.GetParameter("y2", 0d))); break;
            case "Geometry.Circle": context.SetOutput("Circle", new VisionCircle(context.GetParameter("x", 0d), context.GetParameter("y", 0d), context.GetParameter("radius", 10d))); break;
            case "Geometry.Distance": PointDistance(context); break;
            case "Geometry.Angle": GeometryAngle(context); break;
            case "Geometry.Intersection": Intersection(context); break;
            case "Arithmetic.Add": Binary(context, "Add"); break;
            case "Arithmetic.Subtract": Binary(context, "Subtract"); break;
            case "Arithmetic.AbsDiff": Binary(context, "AbsDiff"); break;
            case "Arithmetic.And": Binary(context, "And"); break;
            case "Arithmetic.Or": Binary(context, "Or"); break;
            case "Arithmetic.Xor": Binary(context, "Xor"); break;
            case "Arithmetic.Not": UnaryNot(context); break;
            case "Arithmetic.MaskApply": Mask(context); break;
            case "Statistics.Mean": Statistic(context, "Mean"); break;
            case "Statistics.StdDev": Statistic(context, "StdDev"); break;
            case "Statistics.MinMax": Statistic(context, "MinMax"); break;
            case "Statistics.CountNonZero": Statistic(context, "CountNonZero"); break;
            case "Statistics.PixelRatio": Statistic(context, "PixelRatio"); break;
            case "Statistics.Integral": Integral(context); break;
            case "Statistics.DFT": Dft(context, false); break;
            case "Statistics.IDFT": Dft(context, true); break;
            case "Logic.Compare": LogicCompare(context); break;
            case "Logic.And": LogicBinary(context, true); break;
            case "Logic.Or": LogicBinary(context, false); break;
            case "Logic.Not": context.SetOutput("Result", !context.GetInput<bool>("Value")); break;
            case "Logic.Switch": LogicSwitch(context); break;
            case var collection when collection.StartsWith("Collection.", StringComparison.Ordinal): Collection(context); break;
            case "Draw.Point": case "Draw.Line": case "Draw.Rect": case "Draw.Circle": case "Draw.Text": Draw(context); break;
            default: AnnotatedPassThrough(context, $"{type}: preview", declaredOutputs); break;
        }
        return ValueTask.CompletedTask;
    }

    private static Mat Gray(Mat source)
    {
        var gray = new Mat();
        if (source.NumberOfChannels == 1) source.CopyTo(gray); else CvInvoke.CvtColor(source, gray, source.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);
        return gray;
    }
    private static Mat Color(Mat source) { var output = new Mat(); if (source.NumberOfChannels == 1) CvInvoke.CvtColor(source, output, ColorConversion.Gray2Bgr); else source.CopyTo(output); return output; }
    private static int Odd(int value, int min = 1) { value = Math.Max(min, value); return value % 2 == 0 ? value + 1 : value; }
    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum => Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
    private static void CaptureCamera(NodeExecutionContext c) { using var capture = new VideoCapture(c.GetParameter("index", 0)); var m = new Mat(); if (!capture.Read(m) || m.IsEmpty) { m.Dispose(); throw new InvalidOperationException("无法从相机读取图像，请检查相机编号和占用状态。"); } c.SetOutput("Image", m); }
    private static void ReadVideo(NodeExecutionContext c) { var path = c.ResolveProjectPath(c.GetParameter("path", "images/input/video.mp4")); using var cap = new VideoCapture(path); cap.Set(CapProp.PosFrames, c.GetParameter("frame", 0)); var m = new Mat(); if (!cap.Read(m) || m.IsEmpty) { m.Dispose(); throw new InvalidDataException("无法读取指定视频帧。"); } c.SetOutput("Image", m); }
    private static void Crop(NodeExecutionContext c) { var s = c.GetInput<Mat>("Image"); var r = Rectangle.Intersect(new Rectangle(c.GetParameter("x", 0), c.GetParameter("y", 0), c.GetParameter("width", 100), c.GetParameter("height", 100)), new Rectangle(Point.Empty, s.Size)); if (r.Width <= 0 || r.Height <= 0) throw new ArgumentOutOfRangeException("ROI", "ROI 不在图像范围内。"); var o = new Mat(s, r).Clone(); c.SetOutput("Image", o); }
    private static void Resize(NodeExecutionContext c) { var o = new Mat(); CvInvoke.Resize(c.GetInput<Mat>("Image"), o, new Size(c.GetParameter("width", 640), c.GetParameter("height", 480)), 0, 0, ParseEnum(c.GetParameter("interpolation", "Linear"), Inter.Linear)); c.SetOutput("Image", o); }
    private static void Rotate(NodeExecutionContext c) { var s = c.GetInput<Mat>("Image"); using var matrix = new Mat(); CvInvoke.GetRotationMatrix2D(new PointF((s.Cols - 1) / 2f, (s.Rows - 1) / 2f), c.GetParameter("angle", 90d), c.GetParameter("scale", 1d), matrix); var o = new Mat(); CvInvoke.WarpAffine(s, o, matrix, s.Size); c.SetOutput("Image", o); }
    private static void Flip(NodeExecutionContext c) { var o = new Mat(); var d = c.GetParameter("direction", "Horizontal").ToLowerInvariant() switch { "vertical" => FlipType.Vertical, "both" => FlipType.Both, _ => FlipType.Horizontal }; CvInvoke.Flip(c.GetInput<Mat>("Image"), o, d); c.SetOutput("Image", o); }
    private static void ConvertColor(NodeExecutionContext c, ColorConversion mode) { var o = new Mat(); CvInvoke.CvtColor(c.GetInput<Mat>("Image"), o, mode); c.SetOutput("Image", o); }
    private static void Split(NodeExecutionContext c) { var channels = c.GetInput<Mat>("Image").Split(); if (channels.Length == 0) throw new InvalidOperationException("输入图像没有通道。"); for (var i = 0; i < 4; i++) c.SetOutput($"Channel{i}", channels[Math.Min(i, channels.Length - 1)].Clone()); foreach (var channel in channels) channel.Dispose(); }
    private static void Blur(NodeExecutionContext c) { var o = new Mat(); var k = Math.Max(1, c.GetParameter("kernelSize", 5)); CvInvoke.Blur(c.GetInput<Mat>("Image"), o, new Size(k, k), new Point(-1, -1)); c.SetOutput("Image", o); }
    private static void Box(NodeExecutionContext c) { var o = new Mat(); var k = Math.Max(1, c.GetParameter("kernelSize", 5)); CvInvoke.BoxFilter(c.GetInput<Mat>("Image"), o, DepthType.Default, new Size(k, k), new Point(-1, -1), c.GetParameter("normalize", true)); c.SetOutput("Image", o); }
    private static void Median(NodeExecutionContext c) { var o = new Mat(); CvInvoke.MedianBlur(c.GetInput<Mat>("Image"), o, Odd(c.GetParameter("kernelSize", 5), 3)); c.SetOutput("Image", o); }
    private static void Bilateral(NodeExecutionContext c) { var o = new Mat(); CvInvoke.BilateralFilter(c.GetInput<Mat>("Image"), o, c.GetParameter("diameter", 9), c.GetParameter("sigmaColor", 75d), c.GetParameter("sigmaSpace", 75d)); c.SetOutput("Image", o); }
    private static void Normalize(NodeExecutionContext c) { var o = new Mat(); CvInvoke.Normalize(c.GetInput<Mat>("Image"), o, c.GetParameter("alpha", 0d), c.GetParameter("beta", 255d), NormType.MinMax); c.SetOutput("Image", o); }
    private static void Clahe(NodeExecutionContext c) { using var g = Gray(c.GetInput<Mat>("Image")); var o = new Mat(); CvInvoke.CLAHE(g, c.GetParameter("clipLimit", 2d), new Size(c.GetParameter("tileSize", 8), c.GetParameter("tileSize", 8)), 256, o); c.SetOutput("Image", o); }
    private static void Equalize(NodeExecutionContext c) { using var g = Gray(c.GetInput<Mat>("Image")); var o = new Mat(); CvInvoke.EqualizeHist(g, o); c.SetOutput("Image", o); }
    private static void Histogram(NodeExecutionContext c) { var source = c.GetInput<Mat>("Image"); using var g = Gray(source); using var pixels = g.ToImage<Gray, byte>(); var bins = Math.Clamp(c.GetParameter("bins", 256), 2, 256); var values = new int[bins]; for (var y = 0; y < g.Rows; y++) for (var x = 0; x < g.Cols; x++) values[pixels.Data[y, x, 0] * bins / 256]++; var o = new Mat(240, 512, DepthType.Cv8U, 3); o.SetTo(new MCvScalar(24, 24, 24)); var max = Math.Max(1, values.Max()); for (var i = 1; i < bins; i++) CvInvoke.Line(o, new Point((i - 1) * 511 / (bins - 1), 230 - values[i - 1] * 220 / max), new Point(i * 511 / (bins - 1), 230 - values[i] * 220 / max), new MCvScalar(255, 200, 80), 2); c.SetOutput("Image", o); c.SetOutput("Histogram", values); }
    private static void ConvertScale(NodeExecutionContext c) { var o = new Mat(); CvInvoke.ConvertScaleAbs(c.GetInput<Mat>("Image"), o, c.GetParameter("alpha", 1d), c.GetParameter("beta", 0d)); c.SetOutput("Image", o); }
    private static void AutoThreshold(NodeExecutionContext c, ThresholdType auto) { using var g = Gray(c.GetInput<Mat>("Image")); var o = new Mat(); var t = CvInvoke.Threshold(g, o, 0, 255, ThresholdType.Binary | auto); c.SetOutput("Image", o); c.SetOutput("Threshold", t); }
    private static void Adaptive(NodeExecutionContext c) { using var g = Gray(c.GetInput<Mat>("Image")); var o = new Mat(); CvInvoke.AdaptiveThreshold(g, o, 255, c.GetParameter("method", "Gaussian").Equals("Mean", StringComparison.OrdinalIgnoreCase) ? AdaptiveThresholdType.MeanC : AdaptiveThresholdType.GaussianC, ThresholdType.Binary, Odd(c.GetParameter("blockSize", 11), 3), c.GetParameter("c", 2d)); c.SetOutput("Image", o); }
    private static void InRange(NodeExecutionContext c) { var o = new Mat(); var lo = new ScalarArray(new MCvScalar(c.GetParameter("lower0", 0d), c.GetParameter("lower1", 0d), c.GetParameter("lower2", 0d))); var hi = new ScalarArray(new MCvScalar(c.GetParameter("upper0", 255d), c.GetParameter("upper1", 255d), c.GetParameter("upper2", 255d))); using (lo) using (hi) CvInvoke.InRange(c.GetInput<Mat>("Image"), lo, hi, o); c.SetOutput("Image", o); }
    private static void DistanceTransform(NodeExecutionContext c) { using var g = Gray(c.GetInput<Mat>("Image")); var distance = new Mat(); using var labels = new Mat(); CvInvoke.DistanceTransform(g, distance, labels, ParseEnum(c.GetParameter("distance", "L2"), DistType.L2), c.GetParameter("maskSize", 3), DistLabelType.CComp); var preview = new Mat(); CvInvoke.Normalize(distance, preview, 0, 255, NormType.MinMax, DepthType.Cv8U); distance.Dispose(); c.SetOutput("Image", preview); }
    private static void Watershed(NodeExecutionContext c) { var image = c.GetInput<Mat>("Image"); using var color = Color(image); using var markerGray = Gray(c.GetInput<Mat>("Markers")); var labels = new Mat(); markerGray.ConvertTo(labels, DepthType.Cv32S); CvInvoke.Watershed(color, labels); var preview = new Mat(); CvInvoke.Normalize(labels, preview, 0, 255, NormType.MinMax, DepthType.Cv8U); c.SetOutput("Image", preview); c.SetOutput("Labels", labels); }
    private static void Morph(NodeExecutionContext c, MorphOp op) { var k = Math.Max(1, c.GetParameter("kernelSize", 3)); using var element = CvInvoke.GetStructuringElement(MorphShapes.Rectangle, new Size(k, k), new Point(-1, -1)); var o = new Mat(); CvInvoke.MorphologyEx(c.GetInput<Mat>("Image"), o, op, element, new Point(-1, -1), c.GetParameter("iterations", 1), BorderType.Reflect101, default); c.SetOutput("Image", o); }
    private static void Canny(NodeExecutionContext c) { using var g = Gray(c.GetInput<Mat>("Image")); var o = new Mat(); CvInvoke.Canny(g, o, c.GetParameter("threshold1", 50d), c.GetParameter("threshold2", 150d), Odd(c.GetParameter("aperture", 3), 3)); c.SetOutput("Image", o); }
    private static void Derivative(NodeExecutionContext c, string kind) { using var g = Gray(c.GetInput<Mat>("Image")); using var raw = new Mat(); if (kind == "Laplacian") CvInvoke.Laplacian(g, raw, DepthType.Cv16S, Odd(c.GetParameter("kernelSize", 3))); else if (kind == "Scharr") CvInvoke.Scharr(g, raw, DepthType.Cv16S, c.GetParameter("dx", 1), c.GetParameter("dy", 0)); else CvInvoke.Sobel(g, raw, DepthType.Cv16S, c.GetParameter("dx", 1), c.GetParameter("dy", 0), Odd(c.GetParameter("kernelSize", 3))); var o = new Mat(); CvInvoke.ConvertScaleAbs(raw, o, 0, 1); c.SetOutput("Image", o); }
    private static void FindContours(NodeExecutionContext c) { var source = c.GetInput<Mat>("Image"); using var g = Gray(source); using var work = g.Clone(); using var vv = new VectorOfVectorOfPoint(); using var hierarchy = new Mat(); CvInvoke.FindContours(work, vv, hierarchy, RetrType.External, ChainApproxMethod.ChainApproxSimple); var items = Enumerable.Range(0, vv.Size).Select(i => vv[i].ToArray()).ToList(); var o = Color(source); for (var i = 0; i < vv.Size; i++) CvInvoke.DrawContours(o, vv, i, new MCvScalar(0, 255, 0), 2); Put(o, $"Contours: {items.Count}"); c.SetOutput("Image", o); c.SetOutput("Contours", new VisionContours(items)); c.SetOutput("Count", items.Count); }
    private static void FilterContours(NodeExecutionContext c) { var source = c.GetInput<Mat>("Image"); var all = c.GetInput<VisionContours>("Contours"); var min = c.GetParameter("minArea", 10d); var max = c.GetParameter("maxArea", 1_000_000d); var selected = all.Items.Where(p => Math.Abs(CvInvoke.ContourArea(new VectorOfPoint(p))) is var a && a >= min && a <= max).ToList(); var o = Color(source); using var vv = new VectorOfVectorOfPoint(); foreach (var p in selected) vv.Push(new VectorOfPoint(p)); for (var i = 0; i < vv.Size; i++) CvInvoke.DrawContours(o, vv, i, new MCvScalar(0, 255, 0), 2); Put(o, $"Blobs: {selected.Count}"); c.SetOutput("Image", o); c.SetOutput("Contours", new VisionContours(selected)); c.SetOutput("Count", selected.Count); }
    private static void Connected(NodeExecutionContext c) { using var g = Gray(c.GetInput<Mat>("Image")); var labels = new Mat(); var count = CvInvoke.ConnectedComponents(g, labels); var display = new Mat(); CvInvoke.Normalize(labels, display, 0, 255, NormType.MinMax, DepthType.Cv8U); Put(display, $"Components: {Math.Max(0, count - 1)}"); c.SetOutput("Image", display.Clone()); c.SetOutput("Labels", labels); c.SetOutput("Count", Math.Max(0, count - 1)); display.Dispose(); }
    private void AnalyzeContour(NodeExecutionContext c)
    {
        var source = c.GetInput<Mat>("Image");
        var contours = c.GetInput<VisionContours>("Contours");
        if (contours.Items.Count == 0) throw new InvalidOperationException("轮廓集合为空。");
        var points = contours.Items.OrderByDescending(p => Math.Abs(CvInvoke.ContourArea(new VectorOfPoint(p)))).First();
        using var vector = new VectorOfPoint(points);
        var output = Color(source);
        object result;
        string label;
        switch (type)
        {
            case "Contour.Area": result = Math.Abs(CvInvoke.ContourArea(vector)); label = $"Area: {(double)result:F2}"; break;
            case "Contour.Perimeter": result = CvInvoke.ArcLength(vector, true); label = $"Perimeter: {(double)result:F2}"; break;
            case "Contour.Center":
                var m = CvInvoke.Moments(vector); var center = new VisionPoint(m.M10 / Math.Max(m.M00, double.Epsilon), m.M01 / Math.Max(m.M00, double.Epsilon)); result = center; label = $"Center: {center.X:F1}, {center.Y:F1}"; CvInvoke.Circle(output, new Point((int)center.X, (int)center.Y), 5, new MCvScalar(0, 0, 255), -1); break;
            case "Contour.BoundingRect":
                var rect = CvInvoke.BoundingRectangle(vector); result = rect; label = $"Rect: {rect.Width}x{rect.Height}"; CvInvoke.Rectangle(output, rect, new MCvScalar(0, 255, 0), 2); break;
            case "Contour.MinAreaRect":
                var rotated = CvInvoke.MinAreaRect(vector); var bounds = rotated.MinAreaRect(); result = bounds; label = $"MinRect: {rotated.Size.Width:F1}x{rotated.Size.Height:F1}"; CvInvoke.Polylines(output, Array.ConvertAll(rotated.GetVertices(), Point.Round), true, new MCvScalar(0, 255, 0), 2); break;
            case "Contour.FitEllipse":
                if (points.Length < 5) throw new InvalidOperationException("拟合椭圆至少需要 5 个轮廓点。"); var ellipse = CvInvoke.FitEllipse(vector); result = ellipse; label = $"Ellipse: {ellipse.Size.Width:F1}x{ellipse.Size.Height:F1}"; CvInvoke.Ellipse(output, ellipse, new MCvScalar(0, 255, 0), 2); break;
            case "Contour.MinCircle": case "LineCircle.FitCircle":
                var circle = CvInvoke.MinEnclosingCircle(vector); result = new VisionCircle(circle.Center.X, circle.Center.Y, circle.Radius); label = $"Circle R: {circle.Radius:F2}"; CvInvoke.Circle(output, Point.Round(circle.Center), (int)Math.Round(circle.Radius), new MCvScalar(0, 255, 0), 2); break;
            case "Contour.ConvexHull":
                using (var hull = new VectorOfPoint()) { CvInvoke.ConvexHull(vector, hull); var data = hull.ToArray(); result = new VisionContours([data]); CvInvoke.Polylines(output, data, true, new MCvScalar(0, 255, 0), 2); label = $"Hull points: {data.Length}"; } break;
            case "Contour.ApproxPolygon":
                using (var approx = new VectorOfPoint()) { CvInvoke.ApproxPolyDP(vector, approx, c.GetParameter("epsilon", 3d), true); var data = approx.ToArray(); result = new VisionContours([data]); CvInvoke.Polylines(output, data, true, new MCvScalar(0, 255, 0), 2); label = $"Vertices: {data.Length}"; } break;
            case "Contour.Circularity":
                var area = Math.Abs(CvInvoke.ContourArea(vector)); var perimeter = CvInvoke.ArcLength(vector, true); result = perimeter == 0 ? 0d : 4 * Math.PI * area / (perimeter * perimeter); label = $"Circularity: {(double)result:F4}"; break;
            case "Contour.AspectRatio":
                var ar = CvInvoke.BoundingRectangle(vector); result = ar.Height == 0 ? 0d : ar.Width / (double)ar.Height; label = $"Aspect: {(double)result:F4}"; break;
            case "Contour.Rectangularity":
                var rr = CvInvoke.BoundingRectangle(vector); result = rr.Width * rr.Height == 0 ? 0d : Math.Abs(CvInvoke.ContourArea(vector)) / (rr.Width * rr.Height); label = $"Rectangularity: {(double)result:F4}"; break;
            case "Contour.Moments":
                var moments = CvInvoke.Moments(vector); result = new Dictionary<string, double> { ["m00"] = moments.M00, ["m10"] = moments.M10, ["m01"] = moments.M01, ["mu20"] = moments.Mu20, ["mu02"] = moments.Mu02 }; label = $"m00: {moments.M00:F2}"; break;
            default: result = Array.Empty<object>(); label = "Convexity defects: 0"; break;
        }
        using var all = new VectorOfVectorOfPoint(vector);
        CvInvoke.DrawContours(output, all, 0, new MCvScalar(255, 120, 0), 2);
        Put(output, label);
        c.SetOutput("Image", output);
        c.SetOutput(type == "LineCircle.FitCircle" ? "Circle" : "Result", result);
    }
    private static void Template(NodeExecutionContext c, bool multi) { var image = c.GetInput<Mat>("Image"); var template = c.GetInput<Mat>("Template"); if (template.Cols > image.Cols || template.Rows > image.Rows) throw new ArgumentException("模板尺寸不能大于搜索图像。"); using var result = new Mat(); CvInvoke.MatchTemplate(image, template, result, TemplateMatchingType.CcoeffNormed); var output = Color(image); if (!multi) { double min = 0, max = 0; Point minp = default, maxp = default; CvInvoke.MinMaxLoc(result, ref min, ref max, ref minp, ref maxp); CvInvoke.Rectangle(output, new Rectangle(maxp, template.Size), new MCvScalar(0, 255, 0), 2); Put(output, $"Score: {max:F4}"); c.SetOutput("Image", output); c.SetOutput("Score", max); c.SetOutput("Point", new VisionPoint(maxp.X, maxp.Y)); } else { var threshold = c.GetParameter("threshold", .8d); var points = new List<VisionPoint>(); using var mask = new Mat(); CvInvoke.Threshold(result, mask, threshold, 1, ThresholdType.Binary); using var pixels = mask.ToImage<Gray, byte>(); for (var y = 0; y < mask.Rows && points.Count < c.GetParameter("maxCount", 20); y++) for (var x = 0; x < mask.Cols && points.Count < c.GetParameter("maxCount", 20); x++) if (pixels.Data[y, x, 0] != 0) { points.Add(new VisionPoint(x, y)); CvInvoke.Rectangle(output, new Rectangle(x, y, template.Cols, template.Rows), new MCvScalar(0, 255, 0), 2); } Put(output, $"Matches: {points.Count}"); c.SetOutput("Image", output); c.SetOutput("Points", points); c.SetOutput("Count", points.Count); } }
    private void HoughLines(NodeExecutionContext c) { var source = c.GetInput<Mat>("Image"); using var g = Gray(source); var lines = CvInvoke.HoughLinesP(g, 1, Math.PI / 180, (int)c.GetParameter("threshold", 50d), c.GetParameter("minLength", 30d), c.GetParameter("maxGap", 10d)); var output = Color(source); foreach (var line in lines) CvInvoke.Line(output, line.P1, line.P2, new MCvScalar(0, 0, 255), 2); Put(output, $"Lines: {lines.Length}"); c.SetOutput("Image", output); c.SetOutput("Lines", lines.Select(x => new VisionLine(x.P1.X, x.P1.Y, x.P2.X, x.P2.Y)).ToList()); }
    private static void HoughCircles(NodeExecutionContext c) { var source = c.GetInput<Mat>("Image"); using var g = Gray(source); var circles = CvInvoke.HoughCircles(g, HoughModes.Gradient, c.GetParameter("dp", 1d), c.GetParameter("minDistance", 20d), c.GetParameter("param1", 100d), c.GetParameter("param2", 30d)); var output = Color(source); foreach (var circle in circles) CvInvoke.Circle(output, Point.Round(circle.Center), (int)Math.Round(circle.Radius), new MCvScalar(0, 255, 0), 2); Put(output, $"Circles: {circles.Length}"); c.SetOutput("Image", output); c.SetOutput("Circles", circles.Select(x => new VisionCircle(x.Center.X, x.Center.Y, x.Radius)).ToList()); }
    private static void FitLine(NodeExecutionContext c) { var source = c.GetInput<Mat>("Image"); var contours = c.GetInput<VisionContours>("Contours"); var points = contours.Items.SelectMany(x => x).ToArray(); if (points.Length < 2) throw new InvalidOperationException("拟合直线至少需要两个点。"); var meanX = points.Average(p => p.X); var meanY = points.Average(p => p.Y); var numerator = points.Sum(p => (p.X - meanX) * (p.Y - meanY)); var denominator = points.Sum(p => (p.X - meanX) * (p.X - meanX)); var slope = denominator == 0 ? double.PositiveInfinity : numerator / denominator; VisionLine line; if (double.IsInfinity(slope)) line = new VisionLine(meanX, 0, meanX, source.Rows - 1); else line = new VisionLine(0, meanY - slope * meanX, source.Cols - 1, meanY + slope * (source.Cols - 1 - meanX)); var output = Color(source); CvInvoke.Line(output, new Point((int)line.X1, (int)line.Y1), new Point((int)line.X2, (int)line.Y2), new MCvScalar(0, 255, 0), 2); c.SetOutput("Image", output); c.SetOutput("Line", line); }
    private static void LineAngle(NodeExecutionContext c) { var source = c.GetInput<Mat>("Image"); var line = c.GetInput<VisionLine>("Line"); var angle = Math.Atan2(line.Y2 - line.Y1, line.X2 - line.X1) * 180 / Math.PI; var output = Color(source); CvInvoke.Line(output, new Point((int)line.X1, (int)line.Y1), new Point((int)line.X2, (int)line.Y2), new MCvScalar(0, 255, 0), 2); Put(output, $"Angle: {angle:F2} deg"); c.SetOutput("Image", output); c.SetOutput("Angle", angle); }
    private static void PointDistance(NodeExecutionContext c) { var a = c.GetInput<VisionPoint>("PointA"); var b = c.GetInput<VisionPoint>("PointB"); c.SetOutput("Distance", Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2))); }
    private static void GeometryAngle(NodeExecutionContext c) { var a = c.GetInput<VisionLine>("LineA"); var b = c.GetInput<VisionLine>("LineB"); var aa = Math.Atan2(a.Y2 - a.Y1, a.X2 - a.X1); var ab = Math.Atan2(b.Y2 - b.Y1, b.X2 - b.X1); var degrees = Math.Abs((aa - ab) * 180 / Math.PI) % 180; c.SetOutput("Angle", degrees); }
    private static void Intersection(NodeExecutionContext c) { var a = c.GetInput<VisionLine>("LineA"); var b = c.GetInput<VisionLine>("LineB"); var d = (a.X1 - a.X2) * (b.Y1 - b.Y2) - (a.Y1 - a.Y2) * (b.X1 - b.X2); var parallel = Math.Abs(d) < 1e-12; var x = parallel ? double.NaN : ((a.X1 * a.Y2 - a.Y1 * a.X2) * (b.X1 - b.X2) - (a.X1 - a.X2) * (b.X1 * b.Y2 - b.Y1 * b.X2)) / d; var y = parallel ? double.NaN : ((a.X1 * a.Y2 - a.Y1 * a.X2) * (b.Y1 - b.Y2) - (a.Y1 - a.Y2) * (b.X1 * b.Y2 - b.Y1 * b.X2)) / d; c.SetOutput("Point", new VisionPoint(x, y)); c.SetOutput("Parallel", parallel); }
    private static void Binary(NodeExecutionContext c, string op) { var a = c.GetInput<Mat>("ImageA"); var b = c.GetInput<Mat>("ImageB"); if (a.Size != b.Size || a.NumberOfChannels != b.NumberOfChannels) throw new ArgumentException("两幅输入图像尺寸和通道数必须一致。"); var o = new Mat(); if (op == "Add") CvInvoke.Add(a, b, o); else if (op == "Subtract") CvInvoke.Subtract(a, b, o); else if (op == "AbsDiff") CvInvoke.AbsDiff(a, b, o); else if (op == "And") CvInvoke.BitwiseAnd(a, b, o); else if (op == "Or") CvInvoke.BitwiseOr(a, b, o); else CvInvoke.BitwiseXor(a, b, o); c.SetOutput("Image", o); }
    private static void UnaryNot(NodeExecutionContext c) { var o = new Mat(); CvInvoke.BitwiseNot(c.GetInput<Mat>("Image"), o); c.SetOutput("Image", o); }
    private static void Mask(NodeExecutionContext c) { var s = c.GetInput<Mat>("Image"); using var mask = Gray(c.GetInput<Mat>("Mask")); var o = new Mat(s.Size, s.Depth, s.NumberOfChannels); o.SetTo(new MCvScalar(0)); s.CopyTo(o, mask); c.SetOutput("Image", o); }
    private static void Statistic(NodeExecutionContext c, string kind) { var source = c.GetInput<Mat>("Image"); using var g = Gray(source); object result; string text; if (kind == "Mean") { var v = CvInvoke.Mean(g).V0; result = v; text = $"Mean: {v:F4}"; } else if (kind == "StdDev") { var mean = new MCvScalar(); var std = new MCvScalar(); CvInvoke.MeanStdDev(g, ref mean, ref std); result = std.V0; text = $"StdDev: {std.V0:F4}"; } else if (kind == "CountNonZero") { var v = CvInvoke.CountNonZero(g); result = v; text = $"Count: {v}"; } else if (kind == "PixelRatio") { var v = CvInvoke.CountNonZero(g) / (double)(g.Rows * g.Cols); result = v; text = $"Ratio: {v:P2}"; } else { double min = 0, max = 0; Point minp = default, maxp = default; CvInvoke.MinMaxLoc(g, ref min, ref max, ref minp, ref maxp); result = $"min={min:F4}, max={max:F4}"; text = (string)result; } var o = Color(source); Put(o, text); c.SetOutput("Image", o); c.SetOutput("Result", result); }
    private static void Integral(NodeExecutionContext c) { using var g = Gray(c.GetInput<Mat>("Image")); var sum = new Mat(); CvInvoke.Integral(g, sum, null, null, DepthType.Cv64F, DepthType.Cv64F); var preview = new Mat(); CvInvoke.Normalize(sum, preview, 0, 255, NormType.MinMax, DepthType.Cv8U); c.SetOutput("Image", preview); c.SetOutput("Result", sum); }
    private static void Dft(NodeExecutionContext c, bool inverse) { var source = c.GetInput<Mat>("Image"); using var g = Gray(source); var floatInput = new Mat(); g.ConvertTo(floatInput, DepthType.Cv32F); var transformed = new Mat(); CvInvoke.Dft(floatInput, transformed, inverse ? DxtType.InvScale : DxtType.Forward, 0); var preview = new Mat(); CvInvoke.Normalize(transformed, preview, 0, 255, NormType.MinMax, DepthType.Cv8U); floatInput.Dispose(); c.SetOutput("Image", preview); c.SetOutput("Result", transformed); }
    private static void LogicCompare(NodeExecutionContext c) { var a = Convert.ToDouble(c.GetInput<object>("A"), System.Globalization.CultureInfo.InvariantCulture); var b = Convert.ToDouble(c.GetInput<object>("B"), System.Globalization.CultureInfo.InvariantCulture); var op = c.GetParameter("operator", ">"); c.SetOutput("Result", op switch { ">" => a > b, ">=" => a >= b, "<" => a < b, "<=" => a <= b, "==" => a.Equals(b), "!=" => !a.Equals(b), _ => throw new ArgumentException("不支持的比较符。") }); }
    private static void LogicBinary(NodeExecutionContext c, bool and) { var a = c.GetInput<bool>("A"); var b = c.GetInput<bool>("B"); c.SetOutput("Result", and ? a && b : a || b); }
    private static void LogicSwitch(NodeExecutionContext c) { c.SetOutput("Result", c.GetInput<bool>("Condition") ? c.GetInput<object>("True") : c.GetInput<object>("False")); }
    private void Collection(NodeExecutionContext c) { var items = c.GetInput<object>("Items"); if (items is not System.Collections.IEnumerable enumerable) throw new ArgumentException("输入不是集合。"); var list = enumerable.Cast<object>().ToList(); if (type == "Collection.Sort") list = list.OrderBy(x => x?.ToString(), StringComparer.CurrentCulture).ToList(); if (type == "Collection.Select") list = list.Take(1).ToList(); c.SetOutput("Result", type == "Collection.Count" ? list.Count : list); }
    private void Draw(NodeExecutionContext c) { var o = Color(c.GetInput<Mat>("Image")); var value = c.GetParameter("value", ""); if (type == "Draw.Point") CvInvoke.Circle(o, new Point(o.Cols / 2, o.Rows / 2), 5, new MCvScalar(0, 255, 0), -1); else if (type == "Draw.Line") CvInvoke.Line(o, new Point(0, 0), new Point(o.Cols - 1, o.Rows - 1), new MCvScalar(0, 255, 0), 2); else if (type == "Draw.Rect") CvInvoke.Rectangle(o, new Rectangle(o.Cols / 4, o.Rows / 4, o.Cols / 2, o.Rows / 2), new MCvScalar(0, 255, 0), 2); else if (type == "Draw.Circle") CvInvoke.Circle(o, new Point(o.Cols / 2, o.Rows / 2), Math.Min(o.Cols, o.Rows) / 4, new MCvScalar(0, 255, 0), 2); else Put(o, value); c.SetOutput("Image", o); }
    private static void AnnotatedPassThrough(NodeExecutionContext c, string text, IReadOnlyList<PortDefinition> outputs)
    {
        c.TryGetInput<Mat>("Image", out var source);
        if (source is null) c.TryGetInput<Mat>("ImageA", out source);
        foreach (var port in outputs)
        {
            object value = port.Type switch
            {
                NodeValueType.Mat when source is not null => Annotate(source, text),
                NodeValueType.Mat => throw new NotSupportedException($"此算子需要图像输入：{text}"),
                NodeValueType.Integer => 0,
                NodeValueType.Double => 0d,
                NodeValueType.Boolean => false,
                NodeValueType.String => text,
                NodeValueType.Point => new VisionPoint(0, 0),
                NodeValueType.Points => new List<VisionPoint>(),
                NodeValueType.Line => new VisionLine(0, 0, 0, 0),
                NodeValueType.Circle => new VisionCircle(0, 0, 0),
                NodeValueType.Rectangle => Rectangle.Empty,
                NodeValueType.Contours => new VisionContours([]),
                NodeValueType.Matrix => new[] { 1d, 0, 0, 0, 1, 0, 0, 0, 1 },
                NodeValueType.Collection => Array.Empty<object>(),
                _ => text
            };
            c.SetOutput(port.Name, value);
        }
    }
    private static Mat Annotate(Mat source, string text) { var output = Color(source); Put(output, text); return output; }
    private static void Put(Mat image, string text) { CvInvoke.PutText(image, text, new Point(12, Math.Min(30, image.Rows - 4)), FontFace.HersheySimplex, .65, new MCvScalar(0, 255, 255), 2); }
}
