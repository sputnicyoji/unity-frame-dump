# unity-frame-dump

Export Unity Frame Debugger data to structured JSON for AI-assisted rendering analysis.

Unity Frame Debugger JSON | [English](#english) | [Chinese](#chinese)

---

<a name="english"></a>

## What is this?

Unity's Frame Debugger lets you inspect draw calls visually -- but there's **no public API** to export the data. You can't automate analysis, compare frames over time, or feed data to AI tools.

**unity-frame-dump** solves this by accessing Unity's internal `FrameDebuggerUtility` via reflection, extracting per-event rendering data, cleaning it into a compact schema, and writing it as a JSON file.

### Before vs After

| | Raw Reflection Dump | unity-frame-dump |
|---|-----|-----|
| **Size** | ~5 MB | ~500 KB |
| **Noise** | Matrix4x4 recursive properties, zero-value fields, instance IDs | Clean, only performance-relevant fields |
| **AI-ready** | Blows context window | Fits in 200K context |
| **Readable** | 142K lines | 21K lines |

## Installation

### Via Unity Package Manager (git URL)

Add to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.sputnicyoji.unity-frame-dump": "https://github.com/sputnicyoji/unity-frame-dump.git"
  }
}
```

Or in Unity: `Window > Package Manager > + > Add package from git URL` and paste:

```
https://github.com/sputnicyoji/unity-frame-dump.git
```

### Manual (embedded package)

Clone this repo into your project's `Packages/` folder:

```bash
cd YourProject/Packages
git clone https://github.com/sputnicyoji/unity-frame-dump.git com.sputnicyoji.unity-frame-dump
```

## Usage

### 1. Enable Frame Debugger

`Window > Analysis > Frame Debugger` > click **Enable**

### 2. Open Exporter

`Tools > Performance > Frame Debugger Exporter`

### 3. Diagnose (first time)

Click **Diagnose Limit Setter**. A color-coded checklist shows if the tool works with your Unity version:

- **PASS** (green) -- check passed
- **FAIL** (red) -- something is broken, see detail

| Check | What it tests |
|-------|---------------|
| API Binding | Can find `FrameDebuggerUtility` via reflection |
| count/limit Properties | Can read event count and replay cursor |
| Limit Setter | Can control GPU replay position |
| GPU Replay | Can read per-event data after replay |
| Field Name Resolution | Can read nested struct fields (blend/depth/shader state) |

### 4. Export

| Button | Speed | Output |
|--------|-------|--------|
| **Quick Export** | Instant | Event names, types, GameObjects |
| **Full Export** | ~1s per event | All detail: shader, state, render target, properties |

Output: `{ProjectRoot}/FrameDebuggerExports/fd_{mode}_{timestamp}.json`

### 5. Analyze

Feed the JSON to your AI tool of choice (Claude, ChatGPT, etc.) and ask:

> "Analyze this frame dump. Identify the top 3 rendering bottlenecks and suggest optimizations."

The clean schema is designed for this -- shader distribution, batch break causes, RT timeline, and per-event detail are all structured for machine consumption.

## Output Schema (v2)

<details>
<summary>Click to expand full schema reference</summary>

### Event

```json
{
  "index": 14,
  "name": "ForwardRendererLow/DrawOpaqueObjects/Draw",
  "type": "Mesh",
  "obj": "Character_01",
  "path": "Scene/Entities/Character_01",
  "detail": {
    "geo": [100, 150, 50],
    "draws": 1,
    "instances": 4,
    "shader": "MyGame/PBR",
    "pass": "ForwardLit",
    "keywords": "_ALPHATEST_ON",
    "rt": {
      "name": "_CameraColorAttachment",
      "size": [1080, 1920],
      "format": 4
    },
    "batchBreak": "Objects have different materials.",
    "state": {
      "blend": { "src": "SrcAlpha", "dst": "OneMinusSrcAlpha" }
    },
    "props": {
      "textures": [{ "name": "_MainTex", "tex": "Tex_Character" }],
      "vectors": [{ "name": "_Color", "v": [1, 1, 1, 1] }]
    }
  }
}
```

### Summary

```json
{
  "summary": {
    "totalVertices": 121502,
    "totalTriangles": 114933,
    "drawCalls": 254,
    "uniqueShaders": 18,
    "shaderDistribution": [
      { "shader": "MyShader", "drawCalls": 47, "vertices": 1273 }
    ],
    "batchBreakCauses": [
      { "cause": "Objects have different materials.", "count": 77 }
    ],
    "renderTargets": [
      { "name": "_ShadowTex", "size": [640, 360], "events": [0, 1] }
    ]
  }
}
```

### Field Reference

| Field | Type | Description |
|-------|------|-------------|
| `geo` | [int,int,int] | [vertices, indices, triangles] |
| `draws` | int | Draw call count |
| `instances` | int | GPU instance count (omitted if 0) |
| `shader` | string | Real shader name |
| `pass` | string | Shader pass name |
| `rt` | object | Render target: name, size, format, backBuffer |
| `batchBreak` | string | Batch break reason |
| `state` | object | Non-default render states only |
| `props` | object | Shader properties (textures, vectors, floats, ints) |

</details>

## Data Cleaning

### What's kept
- Geometry, shader identity, render targets, batch break causes
- Render states (non-default only), shader properties (textures, vectors, floats)

### What's dropped
- `Matrix4x4` recursive properties (rotation/inverse/transpose -- #1 bloat source)
- `Vector4` computed properties (normalized/magnitude)
- Compute/ray tracing fields (all zeros if unused)
- Instance IDs, shader matrices, buffers/cBuffers
- Non-draw events (ClearAll, ClearDepthStencil, ResolveRT) do not output `state` or `props` -- their GPU state is inherited from the previous pass and has no analytical value

## Self-Check

The tool probes the first 5 draw events during export to verify data quality across 4 dimensions:

| Dimension | Probes | All-zero means |
|-----------|--------|----------------|
| Vertex | `m_VertexCount > 0` | Geometry field name changed |
| Shader | `m_RealShaderName` non-empty | Shader name field renamed |
| State | blend/raster values non-default | Nested state struct fields renamed |
| Props | textures/vectors/floats non-empty | `m_ShaderInfo` struct changed |

If any dimension fails, a warning appears in both the Unity Console and the JSON output (`root.issues` array).

## Compatibility

| Unity Version | Status |
|---------------|--------|
| 2022.3 LTS | Tested |
| 2021.3 LTS | Expected to work |
| 2020.3 LTS | Expected to work |
| Unity 6+ | May need field name updates |

## How It Works

The tool accesses `UnityEditorInternal.FrameDebuggerUtility` (internal, undocumented) via `System.Reflection`:

1. `GetFrameEvents()` -- get all events
2. `limit = i + 1` -- move GPU replay cursor
3. `RepaintAllViews()` -- trigger GPU re-replay
4. Wait 2 editor frames
5. `GetFrameEventData(i)` -- read populated event data
6. Extract fields with `m_` prefix fallback for nested structs
7. Write clean JSON

All `FieldInfo` lookups are cached (`Dictionary<(Type, string), FieldInfo>`). Zero heap allocations in the per-event hot path (reusable invoke arg arrays).

## License

MIT

---

<a name="chinese"></a>

## unity-frame-dump 中文说明

将 Unity Frame Debugger 数据导出为结构化 JSON，用于 AI 辅助渲染分析。

### 为什么需要这个工具？

Unity 的 Frame Debugger 是纯 GUI 工具 -- 可以看 draw call，但没有公开 API 导出数据。无法自动分析、跨帧对比、给 AI 分析。

**unity-frame-dump** 反射访问 `FrameDebuggerUtility`，提取渲染数据，清洗后输出 JSON。

### 效果对比

| | 原始输出 | unity-frame-dump |
|---|-----|-----|
| **大小** | ~5 MB | ~500 KB |
| **噪音** | Matrix4x4 递归、零值、ID | 仅性能字段 |
| **AI** | 超上下文 | 放入 200K |

## 安装

提供三种安装方式，任选其一：

### 方式一：Unity Package Manager (推荐)

1. 打开 Unity 菜单: `Window > Package Manager`
2. 点击左上角 **+** 按钮
3. 选择 **Add package from git URL...**
4. 粘贴以下地址：

```
https://github.com/sputnicyoji/unity-frame-dump.git
```

5. 点击 **Add**，等待导入完成

### 方式二：手动编辑 manifest.json

打开项目的 `Packages/manifest.json`，在 `dependencies` 中添加一行：

```json
{
  "dependencies": {
    "com.sputnicyoji.unity-frame-dump": "https://github.com/sputnicyoji/unity-frame-dump.git",
    "...": "..."
  }
}
```

保存后 Unity 会自动下载并导入。

### 方式三：克隆到本地 (离线使用)

```bash
cd YourProject/Packages
git clone https://github.com/sputnicyoji/unity-frame-dump.git com.sputnicyoji.unity-frame-dump
```

克隆后 Unity 会自动识别 `Packages/` 下的嵌入式包，无需修改 `manifest.json`。

### 安装后验证

安装成功后，菜单栏会出现: `Tools > Performance > Frame Debugger Exporter`

如果看不到这个菜单，检查 Console 是否有编译错误。

## 使用方法

### 第一步：开启 Frame Debugger

1. 菜单: `Window > Analysis > Frame Debugger`
2. 点击 **Enable** 开始捕获
3. 游戏会暂停，导航到你想分析的帧

### 第二步：打开导出器

菜单: `Tools > Performance > Frame Debugger Exporter`

### 第三步：诊断 (首次使用建议执行)

点击 **Diagnose Limit Setter**，检查工具是否兼容当前 Unity 版本。

结果会用颜色标注：

- **PASS** (绿色) -- 检查通过
- **FAIL** (红色) -- 有问题，查看右侧详情

| 检查项 | 检测内容 | 失败说明 |
|------|------|------|
| API Binding | 反射找 `FrameDebuggerUtility` | Unity 版本不兼容 |
| count/limit | 事件数/回放游标可读 | 属性被移除或重命名 |
| Limit Setter | GPU 回放游标可写 | 异步导出的 detail 数据可能为空 |
| GPU Replay | 回放后能读到事件数据 | 回放机制变化 |
| Field Name | 嵌套结构体字段可读 | 内部字段名变化，需更新代码 |

### 第四步：导出

| 按钮 | 速度 | 输出内容 | 适用场景 |
|------|------|------|------|
| **Quick Export** | 立即 | 事件名、类型、GameObject | 快速概览 |
| **Full Export** | ~1s/事件 | Shader/状态/RT/属性等完整详情 | 深度分析 |

输出文件保存在: `{项目根目录}/FrameDebuggerExports/fd_{mode}_{timestamp}.json`

导出完成后，窗口底部会显示 **Open File** 和 **Reveal in Explorer** 按钮。

### 第五步：AI 分析

将导出的 JSON 文件提供给 AI 工具 (Claude, ChatGPT 等)，示例提问：

> "分析这个帧数据。找出前 3 个渲染瓶颈并建议优化方案。"

清洗后的数据专为 AI 分析设计 -- Shader 分布、Batch Break 原因、RT 时间线、逐事件详情均已结构化。

## 数据自检

导出时自动探测前 5 个绘制事件的 4 个维度：

| 维度 | 探测字段 | 全零说明什么 |
|------|------|------|
| 顶点 | `m_VertexCount > 0` | 几何数据字段名变了 |
| Shader | `m_RealShaderName` 非空 | Shader 名称字段被重命名 |
| 渲染状态 | blend/raster 值非默认 | 嵌套状态结构体字段名变了 |
| Shader 属性 | textures/vectors 非空 | `m_ShaderInfo` 结构变了 |

异常时 Console 和 JSON `root.issues` 同时报警。无 `issues` 字段 = 数据健康。

## 兼容性

| Unity 版本 | 状态 |
|---------|------|
| 2022.3 LTS | ✅ 已测试 |
| 2021.3 LTS | 预期可用 |
| 2020.3 LTS | 预期可用 |
| Unity 6+ | 可能需要更新字段名 |

## 工作原理

通过 `System.Reflection` 访问 Unity 内部类 `UnityEditorInternal.FrameDebuggerUtility`：

1. `GetFrameEvents()` -- 获取所有事件
2. `limit = i + 1` -- 移动 GPU 回放游标
3. `RepaintAllViews()` -- 触发 GPU 重新回放
4. 等待 2 个编辑器帧
5. `GetFrameEventData(i)` -- 读取已填充的事件数据
6. 清洗并输出结构化 JSON

所有 `FieldInfo` 查找均已缓存。热路径零堆分配。

## License

MIT
