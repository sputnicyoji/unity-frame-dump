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
    "com.sputnicyoji.frame-dump": "https://github.com/sputnicyoji/unity-frame-dump.git"
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
git clone https://github.com/sputnicyoji/unity-frame-dump.git com.sputnicyoji.frame-dump
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

`Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.sputnicyoji.frame-dump": "https://github.com/sputnicyoji/unity-frame-dump.git"
  }
}
```

或: `Window > Package Manager > + > Add package from git URL`

## 使用

1. 开 Frame Debugger: `Window > Analysis > Frame Debugger` > **Enable**
2. 开导出器: `Tools > Performance > Frame Debugger Exporter`
3. 首次点 **Diagnose Limit Setter** 检查兼容性
4. 点 **Full Export** 导出
5. JSON 给 AI 分析

### 诊断结果

- **PASS** (绿) -- OK
- **FAIL** (红) -- 查看详情

| 检查项 | 内容 |
|------|------|
| API Binding | 反射找 `FrameDebuggerUtility` |
| count/limit | 属性可读 |
| Limit Setter | GPU 游标可写 |
| GPU Replay | 回放后可读数据 |
| Field Name | 嵌套字段可读 |

### 自检

导出时探测 4 维度 (顶点/Shader/状态/属性)，异常时 Console + `root.issues` 报警。

## 兼容性

| Unity | |
|-------|------|
| 2022.3 LTS | ✅ |
| 2021.3 LTS | 预期可用 |
| 2020.3 LTS | 预期可用 |
| Unity 6+ | 可能需更新 |

## 原理

反射 `FrameDebuggerUtility` 内部 API:

1. `GetFrameEvents()` 获取事件
2. `limit = i+1` 移动游标
3. `RepaintAllViews()` 触发回放
4. 等 2 帧
5. `GetFrameEventData(i)` 读数据
6. 清洗输出

`FieldInfo` 全缓存。零堆分配。

## License

MIT
