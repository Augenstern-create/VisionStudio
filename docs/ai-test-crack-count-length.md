# AI-E2E-CRACK-001：裂纹数量与像素长度组合流程

## 测试输入

| 字段 | 内容 |
| --- | --- |
| 外部项目夹具 | `E:\demo\视觉项目-20260811-2322` |
| 项目文件 | `视觉项目-20260811-2322.vstudio` |
| 输入图片 | `images/input/1.png` |
| 图片引用 | `[IMG-01] 加载图像` |
| 用户需求 | `我需要一个能测量裂纹数量与像素长度的流程` |
| 仓库回归图 | `tests/image/1.png` |

测试不得直接保存外部项目。自动化测试复制图片到临时项目，结束后删除临时数据；外部项目文件、工作流、缓存和输出的前后哈希必须一致。

## 正确规划意图

“裂纹”是目标，不是算子接口名。默认方案应由多个可观察、可替换的通用阶段组成：

```text
[IMG-01] 加载图像
  → 灰度化 Image.Gray
  → 高斯滤波 Image.GaussianBlur
  → 黑帽 Morphology.BlackHat
  → Otsu 自动阈值 Threshold.Otsu
  → 闭运算 Morphology.Close
  → 查找轮廓 Blob.Contours
  → 细长目标路径统计 Contour.PathMetrics
```

黑帽突出比周围暗的细长结构，Otsu 生成候选二值区域，闭运算连接小间断，轮廓阶段提取候选，最终阶段按面积、长度和细长度筛选并输出数量与估算像素长度。具体核大小和筛选阈值允许模型建议、用户调整或后续基于标注集优化。

只有输入明确包含 `Defect.CrackMeasure`、“专用裂纹算子”或“单算子方案”时，才允许使用专项算子替代上述组合。

## 自动化验收

### 规划结构

1. 默认计划不含 `Defect.CrackMeasure`。
2. 至少包含 `Threshold.Otsu`、`Blob.Contours`、`Contour.PathMetrics`；推荐包含完整七阶段链。
3. 模型可返回一条 `add_pipeline`，但 Agent 返回和保存的计划必须已展开成普通节点与连线。
4. 所有新节点具有唯一实例 ID；连接引用实例 ID，不能把 `Image.Gray` 等接口名当节点 ID。
5. `Blob.Contours.Image` 和 `Blob.Contours.Contours` 均连接到 `Contour.PathMetrics` 的对应必需输入。
6. 最终节点输出 `Image`、`Count`、`PixelLength`、`TotalPerimeter` 和 `Summary`。

### 防回归

1. 模型输出未知算子、缺少节点、错误端口或不完整 JSON 时，计划被拒绝并最多修正三次。
2. 三次均失败时 Graph revision、节点和连线数量保持不变；绝不能继续应用最后一次无效计划。
3. 计划整批提交为一次操作记录，`Ctrl+Z` 可完整撤销。
4. 原图路径和加载图像节点保持不变。

### 执行结果

1. 执行成功并生成带候选轮廓/统计文字的预览图。
2. `Count` 是大于等于 0 的整数；`PixelLength` 与 `TotalPerimeter` 是有限且大于等于 0 的数；`Summary` 同时描述数量和长度。
3. 在尚未建立人工标注集前，不对组合流程写死“必须 1 条、560.9 px”。该值属于专项算子的单图回归基线，不等于组合流程真值。
4. 若本用例需要升级为精度测试，应先标注裂纹实例和中心线长度，并按方向、对比度、纹理、分叉与断裂情况拆分数据集和容差。

### 缓存

1. 相同输入和参数第二次执行命中全部七个阶段缓存。
2. 修改黑帽核大小后，灰度和高斯阶段继续命中缓存，黑帽及其下游重新执行。
3. 修改最终筛选阈值时，仅 `Contour.PathMetrics` 及其下游失效。

## 实机模型记录

使用 `E:\models` 中的 Qwen2.5-VL GGUF 与自动发现的 `D:\Programs\llama`。记录模型实际 JSON、重试原因、展开后计划和执行输出。没有匹配的 `mmproj` 时，本用例只证明“本地模型进行文本规划，OpenCV 执行图片处理”，不证明 VLM 直接观察了图片。

执行命令：

```powershell
./scripts/test-local-ai.ps1
```

本用例通过的必要条件是组合规划、受控展开和执行闭环均成功；仅返回一句算法建议或直接尝试不存在的 `Defect.CrackMeasure` 节点均判定失败。
