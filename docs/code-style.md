# Vision Studio 代码风格规范

## 1. 适用范围

仓库以 Google 官方风格指南为基线：

- C#：Google C# Style Guide。
- JavaScript：Google JavaScript Style Guide；新增复杂前端代码优先考虑 TypeScript。
- HTML/CSS：Google HTML/CSS Style Guide。
- Markdown：Google Documentation Style Guide。
- Avalonia XAML、JSON、PowerShell 和 Shell：沿用 Google 的 UTF-8、空格缩进、控制行宽、消除尾随空格和解释“为什么”的原则，并采用本文件约定。

`.editorconfig` 是可执行规则的唯一来源。本文件用于解释无法由编辑器完整表达的规则。

## 2. 通用规则

1. 文件使用 UTF-8 和 LF，文件末尾保留一个换行。
2. 禁止尾随空格和用于缩进的制表符；解决方案文件除外。
3. C#、JavaScript、HTML、CSS、XAML、JSON 和脚本统一使用 2 空格缩进。
4. C# 和 JavaScript 代码行以 100 列为目标上限。长 URL、资源键和无法合理拆分的诊断文本可以例外。
5. 注释解释设计原因、约束或风险，不复述代码。待办事项使用 `TODO: 说明`。
6. 一个提交避免同时进行行为修改和大范围格式化，降低审查噪声。

## 3. C# 规则

### 3.1 命名与文件

- 类型、方法、枚举、命名空间和公开成员使用 `PascalCase`。
- 接口使用 `I` 前缀。
- 参数和局部变量使用 `camelCase`。
- 私有字段，包括 `const`、`static` 和 `readonly` 字段，使用 `_camelCase`。
- 文件使用 `PascalCase.cs`，原则上一个核心类型对应一个文件。
- 缩写按单词处理，例如 `AiModel`、`HttpClient`，不写成 `AIModel` 或 `HTTPClient`。

### 3.2 格式与组织

- 左花括号与声明或控制语句同一行；`else`、`catch`、`finally`紧跟前一个右花括号。
- 即使语句只有一行，`if`、`for`、`foreach`、`while` 也必须使用花括号。
- `using System...` 位于其他 using 之前，各组内按字母排序。
- 成员依次排列为：嵌套类型/事件、静态与只读字段、字段/属性、构造函数、方法；每组内公开成员优先。
- 简单只读属性可以使用表达式体；方法、构造函数和操作符不使用表达式体。
- 类型明显或类型名噪声较大时使用 `var`；基本类型和类型并不明显时显式书写类型。

### 3.3 可维护性

- 能使用 `const` 时不用可变字段，否则优先 `readonly`。
- 输入集合使用满足需求的最窄只读接口，如 `IReadOnlyList<T>`。
- 非平凡或复用的 lambda 提取为命名方法。
- 避免长 LINQ 链和复杂元组返回值；复杂结果使用有名称的类型。
- 委托通过 `SomeDelegate?.Invoke()` 调用。
- 公共 API 的布尔参数含义不明显时改用枚举或选项对象。

## 4. Web 与 XAML 规则

- JavaScript 默认使用 `const`，确需重新赋值时使用 `let`，禁止 `var`。
- HTML 使用语义化元素、`<!doctype html>`、`<meta charset="utf-8">`，图像提供合适的 `alt`。
- 表现、结构与行为分离；避免内联样式、内联事件和不必要的 `id`。
- CSS 选择器和声明分行，声明以分号结束，避免 `!important` 和 ID 选择器。
- XAML 中控件层级使用 2 空格缩进；重复样式进入资源，不在页面内复制大量属性。

## 5. 审查与逐步迁移

运行：

```powershell
./scripts/audit-code-style.ps1
```

当前仓库通过基线方式阻止规范债务继续增加：

```powershell
./scripts/test-changes.ps1 -Dimension CodeStyle -NoBuild
```

基线记录在 `.code-style-baseline.json`。整改后必须下调相应数值，禁止为了让测试通过而上调基线。

默认模式只生成审查结果，不改文件。准备把某个模块纳入强制门禁时运行：

```powershell
./scripts/audit-code-style.ps1 -Strict -Path src/VisionStudio.Core
```

旧代码按模块迁移。每次迁移只修改一个项目，先格式化，再做对应维度测试；禁止在未审阅的情况下全仓自动修复。
