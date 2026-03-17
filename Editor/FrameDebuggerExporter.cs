using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Frame Debugger data exporter.
/// Reflects into FrameDebuggerUtility (internal API) to export per-event rendering data as JSON.
/// Tested on Unity 2022.3 LTS.
/// API lives in: UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility
/// Menu: Tools/Performance/Frame Debugger Exporter
/// </summary>
public class FrameDebuggerExporter : EditorWindow
{
    private static readonly BindingFlags s_Static =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly BindingFlags s_Instance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    #region Constants

    // Render state defaults — omit from output when values match these
    private const int c_DefaultBlendWriteMask = 15;
    private const int c_DefaultBlendSrc = 1;      // One
    private const int c_DefaultBlendDst = 0;       // Zero
    private const int c_DefaultBlendOp = 0;        // Add
    private const int c_DefaultRasterCull = 2;     // Back
    private const int c_DefaultDepthWrite = 1;

    #endregion

    #region Reflection Cache

    private Type m_UtilType;
    private Type m_EventDataType;
    private Type m_EventType;

    // Methods
    private MethodInfo m_GetFrameEvents;          // FrameDebuggerEvent[] GetFrameEvents()
    private MethodInfo m_GetFrameEventData;       // bool GetFrameEventData(int, FrameDebuggerEventData)
    private MethodInfo m_GetFrameEventInfoName;   // string GetFrameEventInfoName(int)
    private MethodInfo m_GetFrameEventObject;     // Object GetFrameEventObject(int)
    private MethodInfo m_GetBatchBreakCauseStrings; // string[] GetBatchBreakCauseStrings()

    // Properties
    private PropertyInfo m_PropCount;
    private PropertyInfo m_PropLimit;
    private MethodInfo m_SetLimitMethod;  // cached set_limit fallback
    private PropertyInfo m_PropReceivingRemote; // receivingRemoteFrameEventData
    private MethodInfo m_GetRemotePlayerGUID;   // stable remote connection check

    // Cached batch break cause lookup table
    private string[] m_BatchBreakCauseStrings;

    // Cached FrameDebuggerEvent field infos (avoid per-event GetField lookups)
    private FieldInfo m_EventTypeField;  // m_Type on FrameDebuggerEvent
    private FieldInfo m_EventObjField;   // m_Obj on FrameDebuggerEvent

    // FieldInfo cache — eliminates ~14000 GetType().GetField() calls per 200-event export
    private readonly Dictionary<(Type, string), FieldInfo> m_FieldCache = new Dictionary<(Type, string), FieldInfo>();

    // Reusable invoke arg arrays — eliminates heap allocations in hot path
    private readonly object[] m_SingleIntArg = new object[1];
    private int m_GetFrameEventDataParamCount = -1;
    private object[] m_EventDataArgs;
    private object m_EventDataScratch;

    // Total event count for current export (used by WriteSummary for both quick and async)
    private int m_TotalEventCount;

    private bool m_TypesDiscovered;

    #endregion

    #region UI State

    private string m_LastExportPath;
    private Vector2 m_ScrollPos;

    #endregion

    #region Export Statistics

    private long m_TotalVertices;
    private long m_TotalIndices;
    private int m_DrawCallCount;
    private Dictionary<string, int> m_ShaderDrawCalls;
    private Dictionary<string, long> m_ShaderVertices;
    private Dictionary<string, int> m_BatchBreakCauses;
    private Dictionary<string, int> m_EventTypeCounts;

    // Render target timeline
    private struct RTSpan
    {
        public string name;
        public int width, height;
        public int startIndex, endIndex;
    }
    private List<RTSpan> m_RTTimeline;
    private string m_LastRTName;

    #endregion

    #region Export Diagnostics

    private readonly HashSet<string> m_ExportIssueKeys = new HashSet<string>();
    private readonly List<string> m_ExportIssues = new List<string>();
    private bool m_LastSummaryAvailable;

    // Self-check: probe first N draw events for data quality
    private const int c_SelfCheckSampleCount = 5;
    private int m_SelfCheckDrawsSeen;
    private int m_SelfCheckVertexHits;
    private int m_SelfCheckShaderHits;
    private int m_SelfCheckStateHits;
    private int m_SelfCheckPropsHits;
    private bool m_SelfCheckReported;

    // Diagnose results for GUI display
    private enum DiagnoseState { NotRun, Running, Pass, Fail }
    private DiagnoseState m_DiagnoseState = DiagnoseState.NotRun;
    private readonly List<(string label, bool ok, string detail)> m_DiagnoseResults
        = new List<(string, bool, string)>();

    #endregion

    #region Async Export State

    private bool m_AsyncExporting;
    private int m_AsyncIndex;
    private int m_AsyncEventCount;
    private int m_AsyncOriginalLimit;
    private int m_AsyncPhase; // 0=set limit, 1..N=wait, N+1=try read, retry if empty
    private int m_AsyncDetailHits;
    private bool m_LimitSetterWorks;
    private Array m_AsyncFrameEventsArray;
    private StringBuilder m_AsyncSb;
    private JsonBuilder m_AsyncJson;
    private bool m_AsyncHeaderWritten;
    private int m_AsyncRetryCount;      // current retry count for this event
    private bool m_LastRemoteState;     // track remote state changes for auto-preset
    private bool m_AsyncIsRemote;       // cached at export start
    private double m_AsyncEventStartTime; // for per-event timeout

    #endregion

    [MenuItem("Tools/Performance/Frame Debugger Exporter")]
    static void ShowWindow()
    {
        var wnd = GetWindow<FrameDebuggerExporter>("FD Exporter");
        wnd.minSize = new Vector2(400, 300);
    }

    private void OnEnable()
    {
        DiscoverTypes();
        m_LastRemoteState = IsRemoteConnected();
    }

    private void OnDisable()
    {
        if (m_AsyncExporting) StopAsyncExport(true);
    }

    #region Type Discovery

    private void DiscoverTypes()
    {
        if (m_TypesDiscovered) return;

        // Search all loaded assemblies for FrameDebugger types
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var type in asm.GetTypes())
                {
                    if (type.FullName == null) continue;

                    if (m_UtilType == null && type.Name == "FrameDebuggerUtility"
                                           && type.FullName.Contains("FrameDebug"))
                        m_UtilType = type;

                    if (m_EventDataType == null && type.Name == "FrameDebuggerEventData")
                        m_EventDataType = type;

                    if (m_EventType == null && type.Name == "FrameDebuggerEvent"
                                            && !type.Name.Contains("Data"))
                        m_EventType = type;
                }
            }
            catch (ReflectionTypeLoadException) { }
        }

        if (m_UtilType != null)
        {
            m_GetFrameEvents = m_UtilType.GetMethod("GetFrameEvents", s_Static);
            m_GetFrameEventData = m_UtilType.GetMethod("GetFrameEventData", s_Static);
            m_GetFrameEventInfoName = m_UtilType.GetMethod("GetFrameEventInfoName", s_Static);
            m_GetFrameEventObject = m_UtilType.GetMethod("GetFrameEventObject", s_Static);
            m_GetBatchBreakCauseStrings = m_UtilType.GetMethod("GetBatchBreakCauseStrings", s_Static);

            m_PropCount = m_UtilType.GetProperty("count", s_Static);
            m_PropLimit = m_UtilType.GetProperty("limit", s_Static);
            m_SetLimitMethod = m_UtilType.GetMethod("set_limit", s_Static);
            m_PropReceivingRemote = m_UtilType.GetProperty("receivingRemoteFrameEventData", s_Static);
            m_GetRemotePlayerGUID = m_UtilType.GetMethod("GetRemotePlayerGUID", s_Static);

            // Pre-cache batch break cause strings
            if (m_GetBatchBreakCauseStrings != null)
            {
                try
                {
                    m_BatchBreakCauseStrings = m_GetBatchBreakCauseStrings.Invoke(null, null) as string[];
                }
                catch (Exception e)
                {
                    ReportIssue("batch-break-strings",
                        $"Failed to read batch break cause strings: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // Cache FrameDebuggerEvent field infos
        if (m_EventType != null)
        {
            m_EventTypeField = m_EventType.GetField("m_Type", s_Instance);
            m_EventObjField = m_EventType.GetField("m_Obj", s_Instance);
        }

        // Cache InvokeGetFrameEventData args
        if (m_GetFrameEventData != null && m_EventDataType != null)
        {
            m_GetFrameEventDataParamCount = m_GetFrameEventData.GetParameters().Length;
            m_EventDataScratch = Activator.CreateInstance(m_EventDataType);
            if (m_GetFrameEventDataParamCount >= 2)
                m_EventDataArgs = new object[] { 0, m_EventDataScratch };
        }

        m_TypesDiscovered = true;
    }

    private int GetCount()
    {
        if (m_PropCount == null) return 0;
        try { return (int)m_PropCount.GetValue(null); }
        catch (Exception e)
        {
            ReportIssue("prop-count", $"Failed to read Frame Debugger count: {e.GetType().Name}: {e.Message}");
            return 0;
        }
    }

    private int GetLimit()
    {
        if (m_PropLimit == null) return 0;
        try { return (int)m_PropLimit.GetValue(null); }
        catch (Exception e)
        {
            ReportIssue("prop-limit", $"Failed to read Frame Debugger limit: {e.GetType().Name}: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Stable remote connection check via GetRemotePlayerGUID.
    /// Returns true if Frame Debugger is connected to a remote device.
    /// </summary>
    private bool IsRemoteConnected()
    {
        if (m_GetRemotePlayerGUID == null) return false;
        try
        {
            var guid = m_GetRemotePlayerGUID.Invoke(null, null);
            if (guid == null) return false;
            string s = guid.ToString();
            // Empty or all-zero GUID = local
            return !string.IsNullOrEmpty(s) && s != "0000000000000000"
                && s != "00000000000000000000000000000000"
                && s != "00000000-0000-0000-0000-000000000000";
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns true while device is actively transferring frame event data.
    /// Use as data-readiness signal: wait for this to go false after triggering replay.
    /// </summary>
    private bool IsTransferringData()
    {
        if (m_PropReceivingRemote == null) return false;
        try
        {
            var val = m_PropReceivingRemote.GetValue(null);
            return val is bool b && b;
        }
        catch { return false; }
    }

    private bool TryValidateExportPrerequisites(bool requireDetailData, out string message)
    {
        var problems = new List<string>();

        if (m_UtilType == null)
            problems.Add("missing FrameDebuggerUtility type");

        if (m_PropCount == null)
            problems.Add("missing count property");

        if (m_GetFrameEvents == null && GetCount() == 0)
            problems.Add("missing GetFrameEvents() fallback");

        if (requireDetailData)
        {
            if (m_GetFrameEventData == null)
                problems.Add("missing GetFrameEventData()");
            if (m_EventDataType == null)
                problems.Add("missing FrameDebuggerEventData type");
            if (m_PropLimit == null)
                problems.Add("missing limit property");
        }

        if (problems.Count == 0)
        {
            message = null;
            return true;
        }

        message = string.Join("\n", problems);
        return false;
    }

    private Array GetFrameEventsArraySafe()
    {
        if (m_GetFrameEvents == null)
            return null;

        try
        {
            return m_GetFrameEvents.Invoke(null, null) as Array;
        }
        catch (Exception e)
        {
            ReportIssue("get-frame-events", $"Failed to invoke GetFrameEvents(): {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private void ResetExportDiagnostics()
    {
        m_ExportIssueKeys.Clear();
        m_ExportIssues.Clear();
        m_LastSummaryAvailable = false;
        m_SelfCheckDrawsSeen = 0;
        m_SelfCheckVertexHits = 0;
        m_SelfCheckShaderHits = 0;
        m_SelfCheckStateHits = 0;
        m_SelfCheckPropsHits = 0;
        m_SelfCheckReported = false;
    }

    private void ReportIssue(string key, string message)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(message))
            return;

        if (!m_ExportIssueKeys.Add(key))
            return;

        m_ExportIssues.Add(message);
    }

    /// <summary>
    /// Probe a draw event's detail data for field readability.
    /// Called on the first N draw events. If key fields are all empty/zero,
    /// it means reflection field names are wrong (e.g., Unity version changed them).
    /// Reports warnings via ReportIssue rather than failing silently.
    /// </summary>
    private void SelfCheckEventData(object eventData, string eventTypeName)
    {
        // Only check actual draw events, skip Clear/Resolve
        if (eventTypeName == null || eventTypeName.StartsWith("Clear") || eventTypeName == "ResolveRT")
            return;

        if (m_SelfCheckDrawsSeen >= c_SelfCheckSampleCount)
        {
            if (!m_SelfCheckReported)
            {
                m_SelfCheckReported = true;

                // Report if any critical field category had zero hits across all samples
                if (m_SelfCheckVertexHits == 0)
                    ReportIssue("selfcheck-vertex",
                        $"[Self-Check] m_VertexCount returned 0 for all {c_SelfCheckSampleCount} sampled draw events. " +
                        "Field name may have changed in this Unity version.");

                if (m_SelfCheckShaderHits == 0)
                    ReportIssue("selfcheck-shader",
                        $"[Self-Check] m_RealShaderName/m_OriginalShaderName returned empty for all {c_SelfCheckSampleCount} sampled draw events. " +
                        "Shader name fields may have been renamed.");

                if (m_SelfCheckStateHits == 0)
                    ReportIssue("selfcheck-state",
                        $"[Self-Check] Blend/Depth/Raster state fields all returned defaults for {c_SelfCheckSampleCount} sampled draw events. " +
                        "State struct field names (m_SrcBlend, m_CullMode, etc.) may have changed.");

                if (m_SelfCheckPropsHits == 0)
                    ReportIssue("selfcheck-props",
                        $"[Self-Check] ShaderInfo (textures/vectors/floats) was empty for all {c_SelfCheckSampleCount} sampled draw events. " +
                        "m_ShaderInfo struct or its sub-fields may have been renamed.");

                if (m_ExportIssues.Count > 0)
                    Debug.LogWarning($"[FDExporter] Self-check detected {m_ExportIssues.Count} data quality issue(s). See JSON root.issues for details.");
            }
            return;
        }

        m_SelfCheckDrawsSeen++;

        // Probe vertex count
        int vc = GetField<int>(eventData, "m_VertexCount");
        if (vc > 0) m_SelfCheckVertexHits++;

        // Probe shader name
        string shader = GetShaderName(eventData);
        if (!string.IsNullOrEmpty(shader)) m_SelfCheckShaderHits++;

        // Probe blend state (non-default src blend means field is readable)
        var blendObj = GetFieldObject(eventData, "m_BlendState");
        if (blendObj != null)
        {
            int src = GetFieldAsInt(blendObj, "srcBlend");
            int dst = GetFieldAsInt(blendObj, "dstBlend");
            int cull = 0;
            var rasterObj = GetFieldObject(eventData, "m_RasterState");
            if (rasterObj != null) cull = GetFieldAsInt(rasterObj, "cullMode");
            // Any non-zero value means the field was successfully read
            if (src != 0 || dst != 0 || cull != 0) m_SelfCheckStateHits++;
        }

        // Probe shader info
        var shaderInfo = GetFieldObject(eventData, "m_ShaderInfo");
        if (shaderInfo != null)
        {
            var textures = GetFieldObject(shaderInfo, "textures") as Array;
            var vectors = GetFieldObject(shaderInfo, "vectors") as Array;
            var floats = GetFieldObject(shaderInfo, "floats") as Array;
            if ((textures != null && textures.Length > 0)
                || (vectors != null && vectors.Length > 0)
                || (floats != null && floats.Length > 0))
                m_SelfCheckPropsHits++;
        }
    }

    private void WriteIssues(JsonBuilder json)
    {
        if (m_ExportIssues.Count == 0)
            return;

        json.Key("issues").BeginArray();
        foreach (string issue in m_ExportIssues)
            json.Value(issue);
        json.EndArray();
    }

    private void WriteSummaryUnavailable(JsonBuilder json, string reason)
    {
        json.Key("summary").BeginObject();
        json.Key("available").Value(false);
        json.Key("reason").Value(reason);
        json.EndObject();
    }

    private static string GetUniqueExportPath(string dir, string mode)
    {
        string baseName = $"fd_{mode}_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
        string fullPath = Path.GetFullPath(Path.Combine(dir, $"{baseName}.json"));
        int suffix = 1;
        while (File.Exists(fullPath))
        {
            fullPath = Path.GetFullPath(Path.Combine(dir, $"{baseName}_{suffix}.json"));
            suffix++;
        }
        return fullPath;
    }

    private bool SetLimit(int value)
    {
        if (m_PropLimit == null) return false;

        // Try 1: Property setter
        if (m_PropLimit.CanWrite)
        {
            try
            {
                m_PropLimit.SetValue(null, value);
                int readBack = GetLimit();
                if (readBack == value) return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FDExporter] Property setter threw: {e.Message}");
            }
        }

        // Try 2: Direct set method (set_limit), cached in DiscoverTypes
        if (m_SetLimitMethod != null)
        {
            try
            {
                m_SetLimitMethod.Invoke(null, new object[] { value });
                int readBack = GetLimit();
                if (readBack == value) return true;
            }
            catch (Exception e)
            {
                ReportIssue("limit-setter",
                    $"Failed to invoke Frame Debugger limit setter: {e.GetType().Name}: {e.Message}");
            }
        }

        return false;
    }

    #endregion

    #region GUI

    private void OnGUI()
    {
        m_ScrollPos = EditorGUILayout.BeginScrollView(m_ScrollPos);

        DrawStatus();
        EditorGUILayout.Space(8);
        DrawOptions();
        EditorGUILayout.Space(12);
        DrawExportButtons();
        DrawDiagnoseResults();
        DrawLastExport();

        EditorGUILayout.EndScrollView();
    }

    private void DrawStatus()
    {
        if (m_UtilType == null)
        {
            EditorGUILayout.HelpBox(
                "FrameDebuggerUtility not found. Click Retry.",
                MessageType.Error);
            if (GUILayout.Button("Retry Type Discovery"))
            {
                m_TypesDiscovered = false;
                DiscoverTypes();
            }
            return;
        }

        if (!TryValidateExportPrerequisites(false, out string quickExportIssue))
        {
            EditorGUILayout.HelpBox(
                "Frame Debugger exporter binding is incomplete:\n" + quickExportIssue,
                MessageType.Error);
            return;
        }

        int count = GetCount();
        if (count == 0)
        {
            EditorGUILayout.HelpBox(
                "Frame Debugger has 0 events.\n\n" +
                "1. Window > Analysis > Frame Debugger\n" +
                "2. Click 'Enable' and pause at target frame\n" +
                "3. Come back here and Export",
                MessageType.Warning);
        }
        else
        {
            int limit = GetLimit();
            EditorGUILayout.HelpBox(
                $"Frame Debugger: {count} events  |  Limit: {limit}",
                MessageType.Info);

            if (!TryValidateExportPrerequisites(true, out string fullExportIssue))
            {
                EditorGUILayout.HelpBox(
                    "Full Export prerequisites are incomplete:\n" + fullExportIssue,
                    MessageType.Warning);
            }
        }
    }

    private GUIStyle m_ModeLabelStyle;

    private void DrawOptions()
    {
        // Auto-detect remote state change
        bool isRemoteNow = IsRemoteConnected();
        if (isRemoteNow != m_LastRemoteState)
            m_LastRemoteState = isRemoteNow;

        // Mode badge — bold, colored, full-width
        if (m_ModeLabelStyle == null)
        {
            m_ModeLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 26
            };
        }

        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = isRemoteNow
            ? new Color(0.2f, 0.7f, 1f)   // blue for remote
            : new Color(0.3f, 0.9f, 0.4f); // green for local
        string label = isRemoteNow ? "REMOTE" : "LOCAL";
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        m_ModeLabelStyle.normal.textColor = isRemoteNow
            ? new Color(0.1f, 0.5f, 1f)
            : new Color(0.1f, 0.6f, 0.2f);
        EditorGUILayout.LabelField(label, m_ModeLabelStyle);
        GUI.backgroundColor = prevBg;

        // Compact info line below badge
        var infoStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
        EditorGUILayout.LabelField(
            isRemoteNow ? "Signal-based wait  |  5s timeout  |  8 retries"
                        : "Frame-based wait  |  1 frame  |  2 retries",
            infoStyle);
        EditorGUILayout.EndVertical();
    }

    private void DrawExportButtons()
    {
        if (m_UtilType == null) return;

        int count = GetCount();
        bool canFullExport = count > 0 && TryValidateExportPrerequisites(true, out _);
        bool canQuickExport = canFullExport || (count > 0 && TryValidateExportPrerequisites(false, out _));

        if (m_AsyncExporting)
        {
            float progress = m_AsyncEventCount > 0 ? (float)m_AsyncIndex / m_AsyncEventCount : 0;
            EditorGUILayout.HelpBox(
                $"Async exporting... {m_AsyncIndex}/{m_AsyncEventCount} ({progress:P0})\n" +
                $"Detail hits so far: {m_AsyncDetailHits}",
                MessageType.Info);
            if (GUILayout.Button("Cancel Async Export"))
                StopAsyncExport(true);
            return;
        }

        GUI.enabled = canQuickExport;

        if (GUILayout.Button("Quick Export (no per-event detail)", GUILayout.Height(30)))
            DoQuickExport();

        EditorGUILayout.Space(4);

        GUI.enabled = canFullExport;

        if (GUILayout.Button("Full Export (async, 1 event/frame)", GUILayout.Height(36)))
            StartAsyncExport();

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Diagnose Limit Setter"))
            DiagnoseLimitSetter();

        GUI.enabled = true;
    }

    private void DrawDiagnoseResults()
    {
        if (m_DiagnoseState == DiagnoseState.NotRun) return;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Diagnose Results", EditorStyles.boldLabel);

        if (m_DiagnoseState == DiagnoseState.Running)
        {
            EditorGUILayout.HelpBox("Running GPU replay tests...", MessageType.Info);
            return;
        }

        foreach (var (label, ok, detail) in m_DiagnoseResults)
        {
            EditorGUILayout.BeginHorizontal();

            // Color indicator
            var prevColor = GUI.color;
            GUI.color = ok ? new Color(0.2f, 0.9f, 0.3f) : new Color(1f, 0.3f, 0.3f);
            GUILayout.Label(ok ? "PASS" : "FAIL", EditorStyles.boldLabel, GUILayout.Width(36));
            GUI.color = prevColor;

            EditorGUILayout.LabelField(label, GUILayout.Width(180));
            EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        // Overall summary
        bool allPass = m_DiagnoseState == DiagnoseState.Pass;
        EditorGUILayout.HelpBox(
            allPass ? "All checks passed. Export should produce complete data."
                    : "Some checks failed. Export may produce incomplete data. See FAIL items above.",
            allPass ? MessageType.Info : MessageType.Warning);
    }

    private void DrawLastExport()
    {
        if (string.IsNullOrEmpty(m_LastExportPath)) return;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Last Export", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel(m_LastExportPath, EditorStyles.miniLabel, GUILayout.Height(18));

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Open File"))
            System.Diagnostics.Process.Start(m_LastExportPath);
        if (GUILayout.Button("Copy Path"))
            EditorGUIUtility.systemCopyBuffer = m_LastExportPath;
        if (GUILayout.Button("Reveal in Explorer"))
            EditorUtility.RevealInFinder(m_LastExportPath);
        EditorGUILayout.EndHorizontal();
    }

    #endregion

    #region Core Export

    /// <summary>
    /// Dumps all FrameDebuggerUtility methods, then tests GPU replay triggers.
    /// </summary>
    private void DiagnoseLimitSetter()
    {
        m_DiagnoseResults.Clear();
        m_DiagnoseState = DiagnoseState.Running;

        int count = GetCount();

        // Check 0: connection mode
        bool remote = IsRemoteConnected();
        string guid = "";
        if (m_GetRemotePlayerGUID != null)
        {
            try { guid = m_GetRemotePlayerGUID.Invoke(null, null)?.ToString() ?? ""; }
            catch { guid = "error"; }
        }
        int diagWait = remote ? 4 : 2;
        int diagRetry = remote ? 8 : 2;
        m_DiagnoseResults.Add(("Connection Mode", true,
            remote ? $"Remote device (GUID={guid})"
                   : $"Local editor (GUID={guid})"));

        // Check 1: API binding
        bool apiOk = m_UtilType != null && m_GetFrameEventData != null && m_EventDataType != null;
        m_DiagnoseResults.Add(("API Binding", apiOk,
            apiOk ? $"FrameDebuggerUtility: {m_UtilType.FullName}"
                  : "FrameDebuggerUtility or EventData type not found"));

        // Check 2: count/limit properties
        bool propsOk = m_PropCount != null && m_PropLimit != null;
        m_DiagnoseResults.Add(("count/limit Properties", propsOk,
            propsOk ? $"count={count}, limit={GetLimit()}"
                    : "count or limit property not found"));

        // Check 3: limit setter
        bool limitOk = false;
        if (propsOk && count > 0)
        {
            int original = GetLimit();
            limitOk = SetLimit(5);
            SetLimit(original);
        }
        m_DiagnoseResults.Add(("Limit Setter", limitOk,
            limitOk ? "SetLimit(5) succeeded, readback confirmed"
                    : count == 0 ? "No events to test (enable Frame Debugger first)"
                    : "SetLimit failed - async export detail data may be empty"));

        if (count < 5 || !apiOk)
        {
            m_DiagnoseState = m_DiagnoseResults.TrueForAll(r => r.ok) ? DiagnoseState.Pass : DiagnoseState.Fail;
            Repaint();
            return;
        }

        // Check 4+5: GPU replay test (async, needs frame delay + retry)
        // Pick probe indices based on actual event count
        int probeA = 4;
        int probeB = Math.Min(count - 1, 49); // don't probe beyond available events
        int restoreLimit = GetLimit();
        SetLimit(probeA + 1);
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

        // Check 4: probe early event
        ProbeWithRetry(probeA, diagWait, diagRetry, (ok4, vc4, shader4) =>
        {
            m_DiagnoseResults.Add(($"GPU Replay (event {probeA})", ok4,
                ok4 ? $"verts={vc4}, shader=\"{shader4}\""
                    : "GetFrameEventData returned false or empty"));

            // Check 5: probe deeper event
            SetLimit(probeB + 1);
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

            ProbeWithRetry(probeB, diagWait, diagRetry, (ok5, vc5, shader5) =>
            {
                m_DiagnoseResults.Add(($"GPU Replay (event {probeB})", ok5,
                    ok5 ? $"verts={vc5}, shader=\"{shader5}\""
                        : "GetFrameEventData returned false or empty"));

                // Check 6: field name readability (m_ prefix test)
                bool fieldOk = false;
                if (ok5 || ok4)
                {
                    int testIdx = ok5 ? probeB : probeA;
                    var testData = InvokeGetFrameEventData(testIdx);
                    if (testData != null)
                    {
                        string sn = GetShaderName(testData);
                        var blend = GetFieldObject(testData, "m_BlendState");
                        int src = blend != null ? GetFieldAsInt(blend, "srcBlend") : -1;
                        var si = GetFieldObject(testData, "m_ShaderInfo");
                        bool siOk = si != null && GetFieldObject(si, "textures") is Array;
                        fieldOk = !string.IsNullOrEmpty(sn) || src >= 0 || siOk;
                    }
                }
                m_DiagnoseResults.Add(("Field Name Resolution", fieldOk,
                    fieldOk ? "Nested struct fields (m_BlendState.srcBlend, m_ShaderInfo.textures) readable"
                            : "Nested struct fields returned null/default - field names may have changed"));

                SetLimit(restoreLimit);
                m_DiagnoseState = m_DiagnoseResults.TrueForAll(r => r.ok) ? DiagnoseState.Pass : DiagnoseState.Fail;
                Repaint();
            });
        });
    }

    /// <summary>
    /// Wait N frames via EditorApplication.delayCall, then invoke the callback.
    /// </summary>
    private static void WaitFramesThenRun(int frames, Action callback)
    {
        int waited = 0;
        EditorApplication.CallbackFunction tick = null;
        tick = () =>
        {
            if (++waited < frames) { EditorApplication.delayCall += tick; return; }
            callback();
        };
        EditorApplication.delayCall += tick;
    }

    /// <summary>
    /// Wait for GPU replay, then probe event data. Retry up to maxRetries times if data is empty.
    /// </summary>
    private void ProbeWithRetry(int index, int waitFrames, int maxRetries,
        Action<bool, int, string> callback)
    {
        WaitFramesThenRun(waitFrames, () =>
        {
            var (ok, vc, shader) = ProbeEventData(index);
            if (!ok && maxRetries > 0)
            {
                // Re-trigger replay and retry next frame
                SetLimit(index + 1);
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                ProbeWithRetry(index, 1, maxRetries - 1, callback);
                return;
            }
            callback(ok, vc, shader);
        });
    }

    private (bool ok, int verts, string shader) ProbeEventData(int index)
    {
        var data = InvokeGetFrameEventData(index);
        if (data == null) return (false, 0, "");
        int vc = GetField<int>(data, "m_VertexCount");
        string sn = GetShaderName(data);
        return (vc > 0 || !string.IsNullOrEmpty(sn), vc, sn ?? "");
    }

    /// <summary>
    /// Quick export: event names, types, gameObjects — no per-event detail data.
    /// </summary>
    private void DoQuickExport()
    {
        ResetExportDiagnostics();
        if (!TryValidateExportPrerequisites(false, out string prerequisiteIssue))
        {
            EditorUtility.DisplayDialog("Export Unavailable",
                "Quick export prerequisites are incomplete:\n" + prerequisiteIssue,
                "OK");
            return;
        }

        Array frameEventsArray = GetFrameEventsArraySafe();
        int eventCount = GetCount();
        if (eventCount == 0 && frameEventsArray != null)
            eventCount = frameEventsArray.Length;
        if (eventCount == 0)
        {
            EditorUtility.DisplayDialog("No Events", "Enable Frame Debugger first.", "OK");
            return;
        }

        ResetStats();
        m_TotalEventCount = eventCount;
        m_LastSummaryAvailable = false;
        var sb = new StringBuilder(1024 * 64);
        var json = new JsonBuilder(sb);

        json.BeginObject();
        WriteHeader(json, eventCount);
        json.Key("events").BeginArray();

        for (int i = 0; i < eventCount; i++)
        {
            json.BeginObject();
            json.Key("index").Value(i);
            WriteEventName(json, i);
            if (frameEventsArray != null && i < frameEventsArray.Length)
                WriteBasicEventInfo(json, frameEventsArray.GetValue(i), i);
            json.EndObject();
        }

        json.EndArray();
        WriteSummaryUnavailable(json, "Quick export skips per-event detail, so aggregate GPU stats are unavailable.");
        WriteIssues(json);
        json.EndObject();

        SaveExport(sb, eventCount, "quick");
    }

    /// <summary>
    /// Async export: steps through events one per editor frame to allow Frame Debugger
    /// to replay each event's GPU state before reading detail data.
    /// </summary>
    private void StartAsyncExport()
    {
        ResetExportDiagnostics();
        if (!TryValidateExportPrerequisites(true, out string prerequisiteIssue))
        {
            EditorUtility.DisplayDialog("Full Export Unavailable",
                "Full export prerequisites are incomplete:\n" + prerequisiteIssue,
                "OK");
            return;
        }

        m_AsyncEventCount = GetCount();
        if (m_AsyncEventCount == 0)
        {
            var arr = GetFrameEventsArraySafe();
            if (arr != null) m_AsyncEventCount = arr.Length;
        }
        if (m_AsyncEventCount == 0)
        {
            EditorUtility.DisplayDialog("No Events", "Enable Frame Debugger first.", "OK");
            return;
        }

        ResetStats();
        m_TotalEventCount = m_AsyncEventCount;
        m_AsyncOriginalLimit = GetLimit();

        // Test limit setter once
        m_LimitSetterWorks = SetLimit(1);
        SetLimit(m_AsyncOriginalLimit); // restore
        if (!m_LimitSetterWorks)
        {
            ReportIssue("limit-setter-unavailable",
                "Full export was aborted because Frame Debugger limit could not be advanced reliably.");
            EditorUtility.DisplayDialog("Full Export Unavailable",
                "Cannot advance Frame Debugger limit reliably in this Unity build.\n\n" +
                "Full export would produce untrustworthy per-event detail.\n" +
                "Use Quick Export or Diagnose Limit Setter instead.",
                "OK");
            return;
        }

        m_AsyncIndex = 0;
        m_AsyncPhase = 0;
        m_AsyncRetryCount = 0;
        m_AsyncIsRemote = IsRemoteConnected();
        m_AsyncDetailHits = 0;
        m_AsyncExporting = true;
        m_AsyncHeaderWritten = false;
        m_LastSummaryAvailable = true;

        Debug.Log($"[FDExporter] Limit setter works: {m_LimitSetterWorks}. Starting async export of {m_AsyncEventCount} events.");

        m_AsyncSb = new StringBuilder(1024 * 256);
        m_AsyncJson = new JsonBuilder(m_AsyncSb);
        m_AsyncFrameEventsArray = GetFrameEventsArraySafe();

        EditorApplication.update += AsyncExportTick;
        Repaint();
    }

    private void AsyncExportTick()
    {
        if (!m_AsyncExporting) { EditorApplication.update -= AsyncExportTick; return; }

        try
        {
            // Write header + start events array on first tick
            if (!m_AsyncHeaderWritten)
            {
                m_AsyncJson.BeginObject();
                WriteHeader(m_AsyncJson, m_AsyncEventCount);
                m_AsyncJson.Key("exportMode").Value("async");
                m_AsyncJson.Key("events").BeginArray();
                m_AsyncHeaderWritten = true;
            }

            int i = m_AsyncIndex;

            if (m_AsyncPhase == 0)
            {
                // Phase 0: Set limit and force GPU replay via view repaint
                SetLimit(i + 1);
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                m_AsyncPhase = 1;
                m_AsyncEventStartTime = EditorApplication.timeSinceStartup;

                // Update progress bar
                string mode = m_AsyncIsRemote ? "Remote" : "Local";
                if (i % 5 == 0)
                    EditorUtility.DisplayProgressBar($"Async Export ({mode})",
                        $"Event {i + 1}/{m_AsyncEventCount} (details: {m_AsyncDetailHits})",
                        (float)i / m_AsyncEventCount);
                return;
            }

            // Phase 1+: wait for data readiness
            if (m_AsyncPhase == 1)
            {
                double elapsed = EditorApplication.timeSinceStartup - m_AsyncEventStartTime;

                if (m_AsyncIsRemote)
                {
                    // Remote: wait for data transfer to complete (signal-based)
                    bool transferring = IsTransferringData();
                    if (transferring && elapsed < 5.0)
                        return; // device still sending, keep waiting (up to 5s)
                    if (!transferring && elapsed < 0.05)
                        return; // too early, give device time to start transfer
                }
                else
                {
                    // Local: 1 frame wait is sufficient
                    if (elapsed < 0.001)
                        return;
                }
                m_AsyncPhase = 2; // ready to read
            }

            // Phase 2: try reading data for event i
            var eventData = InvokeGetFrameEventData(i);

            // Retry: if read failed, re-trigger replay and wait again (max 8 for remote, 2 for local)
            int maxRetry = m_AsyncIsRemote ? 8 : 2;
            if (eventData == null && m_AsyncRetryCount < maxRetry)
            {
                m_AsyncRetryCount++;
                SetLimit(i + 1);
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                m_AsyncPhase = 1;
                m_AsyncEventStartTime = EditorApplication.timeSinceStartup;
                return;
            }

            // Write event (with or without detail)
            m_AsyncJson.BeginObject();
            m_AsyncJson.Key("index").Value(i);
            WriteEventName(m_AsyncJson, i);
            string evtType = null;
            if (m_AsyncFrameEventsArray != null && i < m_AsyncFrameEventsArray.Length)
                evtType = WriteBasicEventInfo(m_AsyncJson, m_AsyncFrameEventsArray.GetValue(i), i);

            if (eventData != null)
            {
                m_AsyncJson.Key("detail");
                WriteCleanDetail(m_AsyncJson, eventData, evtType);
                CollectStats(eventData, i);
                SelfCheckEventData(eventData, evtType);
                m_AsyncDetailHits++;
            }

            m_AsyncJson.EndObject();

            // Move to next event
            m_AsyncIndex = i + 1;
            m_AsyncPhase = 0;
            m_AsyncRetryCount = 0;

            if (m_AsyncIndex >= m_AsyncEventCount)
                StopAsyncExport(false);
            else
                Repaint(); // Keep GUI updating
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            StopAsyncExport(true);
        }
    }

    private void StopAsyncExport(bool cancelled)
    {
        EditorApplication.update -= AsyncExportTick;
        EditorUtility.ClearProgressBar();

        // Restore original limit
        SetLimit(m_AsyncOriginalLimit);

        if (cancelled)
        {
            m_AsyncExporting = false;
            Debug.Log("[FDExporter] Export cancelled.");
            Repaint();
            return;
        }

        // Finish JSON
        if (m_AsyncDetailHits == 0 && m_AsyncEventCount > 0)
        {
            ReportIssue("detail-hits-zero",
                "Full export completed without any per-event detail payloads. Internal Frame Debugger API may have changed.");
        }

        m_AsyncJson.EndArray();
        WriteSummary(m_AsyncJson);
        WriteIssues(m_AsyncJson);
        m_AsyncJson.EndObject();

        SaveExport(m_AsyncSb, m_AsyncEventCount, "async");

        m_AsyncExporting = false;
        Repaint();

        Debug.Log($"[FDExporter] Async export complete. " +
                  $"Events: {m_AsyncEventCount}, Detail hits: {m_AsyncDetailHits}");
    }

    private void WriteHeader(JsonBuilder json, int eventCount)
    {
        json.Key("header").BeginObject();
        json.Key("version").Value(2);
        json.Key("exportTime").Value(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        json.Key("unityVersion").Value(Application.unityVersion);
        json.Key("platform").Value(Application.platform.ToString());
        json.Key("gpu").Value(SystemInfo.graphicsDeviceName);
        json.Key("api").Value(SystemInfo.graphicsDeviceType.ToString());
        json.Key("resolution").BeginArray().Value(Screen.width).Value(Screen.height).EndArray();
        json.Key("totalEvents").Value(eventCount);
        json.EndObject();
    }

    private void WriteEventName(JsonBuilder json, int index)
    {
        if (m_GetFrameEventInfoName == null) return;
        try
        {
            m_SingleIntArg[0] = index;
            string name = m_GetFrameEventInfoName.Invoke(null, m_SingleIntArg) as string;
            if (!string.IsNullOrEmpty(name))
                json.Key("name").Value(ShortenEventName(name));
        }
        catch (Exception e)
        {
            ReportIssue("event-name-read",
                $"Failed to read Frame Debugger event name: {e.GetType().Name}: {e.Message}");
        }
    }

    private void SaveExport(StringBuilder sb, int eventCount, string mode)
    {
        string dir = Path.Combine(Application.dataPath, "..", "FrameDebuggerExports");
        Directory.CreateDirectory(dir);
        string fullPath = GetUniqueExportPath(dir, mode);
        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);

        m_LastExportPath = fullPath;

        var fileInfo = new FileInfo(fullPath);
        string summaryLine = m_LastSummaryAvailable
            ? $"Draw Calls: {m_DrawCallCount}\n" +
              $"Total Vertices: {m_TotalVertices:N0}\n" +
              $"Total Triangles: {m_TotalIndices / 3:N0}\n" +
              $"Unique Shaders: {m_ShaderDrawCalls.Count}\n"
            : "Summary: unavailable in quick export mode\n";
        string issueLine = m_ExportIssues.Count > 0
            ? $"\nIssues: {m_ExportIssues.Count} (see JSON root.issues)\n"
            : "\nIssues: 0\n";

        EditorUtility.DisplayDialog("Export Complete",
            $"Mode: {mode}\n" +
            $"Events: {eventCount}\n" +
            summaryLine +
            issueLine +
            $"File Size: {fileInfo.Length / 1024f:F1} KB\n\n" +
            fullPath,
            "OK");
    }

    /// <returns>Event type name (e.g. "Mesh", "ClearAll") for downstream filtering.</returns>
    private string WriteBasicEventInfo(JsonBuilder json, object evt, int index)
    {
        string eventTypeName = null;
        if (m_EventTypeField != null)
        {
            try
            {
                var val = m_EventTypeField.GetValue(evt);
                eventTypeName = val?.ToString();
                if (!string.IsNullOrEmpty(eventTypeName))
                {
                    json.Key("type").Value(eventTypeName);
                    m_EventTypeCounts.TryGetValue(eventTypeName, out int c);
                    m_EventTypeCounts[eventTypeName] = c + 1;
                }
            }
            catch (Exception e)
            {
                ReportIssue("event-type-read",
                    $"Failed to read Frame Debugger event type: {e.GetType().Name}: {e.Message}");
            }
        }

        if (m_EventObjField != null)
        {
            try
            {
                var obj = m_EventObjField.GetValue(evt) as UnityEngine.Object;
                if (obj != null)
                {
                    json.Key("obj").Value(obj.name);

                    if (obj is GameObject go)
                        json.Key("path").Value(GetTransformPath(go.transform));
                    else if (obj is Component comp)
                        json.Key("path").Value(GetTransformPath(comp.transform));
                }
            }
            catch (Exception e)
            {
                ReportIssue("event-object-read",
                    $"Failed to read Frame Debugger event object: {e.GetType().Name}: {e.Message}");
            }
        }

        if (m_EventObjField == null && m_GetFrameEventObject != null)
        {
            try
            {
                m_SingleIntArg[0] = index;
                var obj = m_GetFrameEventObject.Invoke(null, m_SingleIntArg) as UnityEngine.Object;
                if (obj != null)
                    json.Key("obj").Value(obj.name);
            }
            catch (Exception e)
            {
                ReportIssue("event-object-fallback-read",
                    $"Failed to invoke GetFrameEventObject(): {e.GetType().Name}: {e.Message}");
            }
        }
        return eventTypeName;
    }

    private object InvokeGetFrameEventData(int index)
    {
        if (m_GetFrameEventData == null || m_EventDataType == null)
            return null;

        try
        {
            if (m_GetFrameEventDataParamCount == 1)
            {
                m_SingleIntArg[0] = index;
                return m_GetFrameEventData.Invoke(null, m_SingleIntArg);
            }

            if (m_GetFrameEventDataParamCount >= 2 && m_EventDataArgs != null)
            {
                m_EventDataArgs[0] = index;
                m_EventDataArgs[1] = m_EventDataScratch;
                var result = m_GetFrameEventData.Invoke(null, m_EventDataArgs);
                if (result is bool success && !success)
                    return null;
                return m_EventDataArgs[1];
            }
        }
        catch (Exception e)
        {
            ReportIssue("event-data-read",
                $"Failed to read per-event detail data: {e.GetType().Name}: {e.Message}");
        }

        return null;
    }

    #endregion

    #region Clean Serialization

    /// <summary>
    /// Write cleaned, structured event detail. Replaces generic DumpObject.
    /// Only outputs fields with analytical value for rendering/performance.
    /// </summary>
    /// <param name="eventTypeName">Event type string (e.g. "Mesh", "ClearAll") for filtering non-draw state noise.</param>
    private void WriteCleanDetail(JsonBuilder json, object eventData, string eventTypeName)
    {
        json.BeginObject();

        // --- Geometry ---
        int vc = GetField<int>(eventData, "m_VertexCount");
        int ic = GetField<int>(eventData, "m_IndexCount");
        int dc = GetField<int>(eventData, "m_DrawCallCount");
        int inst = GetField<int>(eventData, "m_InstanceCount");

        if (vc > 0 || ic > 0)
        {
            json.Key("geo").BeginArray();
            json.Value(vc).Value(ic).Value(ic / 3);
            json.EndArray();
        }
        if (dc > 0) json.Key("draws").Value(dc);
        if (inst > 0) json.Key("instances").Value(inst);

        // --- Shader ---
        string shader = GetShaderName(eventData);
        if (!string.IsNullOrEmpty(shader))
        {
            json.Key("shader").Value(shader);

            string pass = GetField<string>(eventData, "m_PassName");
            if (!string.IsNullOrEmpty(pass))
                json.Key("pass").Value(pass);

            string lightMode = GetField<string>(eventData, "m_PassLightMode");
            if (!string.IsNullOrEmpty(lightMode))
                json.Key("lightMode").Value(lightMode);

            string kw = GetField<string>(eventData, "shaderKeywords");
            if (!string.IsNullOrEmpty(kw))
                json.Key("keywords").Value(kw);
        }

        // --- Render Target ---
        WriteCleanRenderTarget(json, eventData);

        // --- Batch Break ---
        int causeInt = GetField<int>(eventData, "m_BatchBreakCause");
        if (causeInt > 0 && m_BatchBreakCauseStrings != null
            && causeInt < m_BatchBreakCauseStrings.Length)
        {
            string cause = m_BatchBreakCauseStrings[causeInt];
            if (!string.IsNullOrEmpty(cause))
                json.Key("batchBreak").Value(cause);
        }

        // --- Render States & Shader Props (draw events only) ---
        // Non-draw events (ClearAll, ClearDepthStencil, etc.) carry leftover GPU state
        // from the previous pass — not meaningful for analysis, just noise.
        bool isDrawEvent = eventTypeName != null
            && !eventTypeName.StartsWith("Clear")
            && eventTypeName != "ResolveRT";

        if (isDrawEvent)
        {
            WriteNonDefaultStates(json, eventData);
            WriteCleanShaderInfo(json, eventData);
        }

        json.EndObject();
    }

    private void WriteCleanRenderTarget(JsonBuilder json, object eventData)
    {
        string rtName = GetField<string>(eventData, "m_RenderTargetName");
        if (string.IsNullOrEmpty(rtName)) return;

        json.Key("rt").BeginObject();
        json.Key("name").Value(rtName);

        int rtW = GetField<int>(eventData, "m_RenderTargetWidth");
        int rtH = GetField<int>(eventData, "m_RenderTargetHeight");
        if (rtW > 0 && rtH > 0)
            json.Key("size").BeginArray().Value(rtW).Value(rtH).EndArray();

        int rtFmt = GetField<int>(eventData, "m_RenderTargetFormat");
        if (rtFmt > 0)
            json.Key("format").Value(rtFmt);

        bool isBack = GetField<bool>(eventData, "m_RenderTargetIsBackBuffer");
        if (isBack)
            json.Key("backBuffer").Value(true);

        int loadAction = GetField<int>(eventData, "m_RenderTargetLoadAction");
        int storeAction = GetField<int>(eventData, "m_RenderTargetStoreAction");
        if (loadAction != 0) json.Key("load").Value(loadAction);
        if (storeAction != 0) json.Key("store").Value(storeAction);

        json.EndObject();
    }

    private void WriteNonDefaultStates(JsonBuilder json, object eventData)
    {
        bool hasState = false;

        // NOTE: Blend/raster/depth fields are enum types internally.
        // Must use GetFieldAsInt (Convert.ToInt32) — C# unboxing requires exact type match.
        var blendObj = GetFieldObject(eventData, "m_BlendState");
        if (blendObj != null)
        {
            int wm = GetFieldAsInt(blendObj, "writeMask");
            int src = GetFieldAsInt(blendObj, "srcBlend");
            int dst = GetFieldAsInt(blendObj, "dstBlend");
            int op = GetFieldAsInt(blendObj, "blendOp");

            bool isDefault = wm == c_DefaultBlendWriteMask
                             && src == c_DefaultBlendSrc
                             && dst == c_DefaultBlendDst
                             && op == c_DefaultBlendOp;

            // Output when any blend param is non-default (including writeMask-only changes)
            if (!isDefault)
            {
                if (!hasState) { json.Key("state").BeginObject(); hasState = true; }
                json.Key("blend").BeginObject();
                json.Key("src").Value(GetEnumString(blendObj, "srcBlend"));
                json.Key("dst").Value(GetEnumString(blendObj, "dstBlend"));
                if (op != 0)
                    json.Key("op").Value(GetEnumString(blendObj, "blendOp"));
                int srcA = GetFieldAsInt(blendObj, "srcBlendAlpha");
                int dstA = GetFieldAsInt(blendObj, "dstBlendAlpha");
                if (srcA != src || dstA != dst)
                {
                    json.Key("srcA").Value(GetEnumString(blendObj, "srcBlendAlpha"));
                    json.Key("dstA").Value(GetEnumString(blendObj, "dstBlendAlpha"));
                }
                int opA = GetFieldAsInt(blendObj, "blendOpAlpha");
                if (opA != op)
                    json.Key("opA").Value(GetEnumString(blendObj, "blendOpAlpha"));
                if (wm != c_DefaultBlendWriteMask)
                    json.Key("writeMask").Value(wm);
                json.EndObject();
            }
        }

        var depthObj = GetFieldObject(eventData, "m_DepthState");
        if (depthObj != null)
        {
            int dw = GetFieldAsInt(depthObj, "depthWrite");
            string df = GetEnumString(depthObj, "depthFunc");
            bool isClearOrNonDraw = dw == 0 && df.StartsWith("Disabled");
            bool isStandardDraw = dw == c_DefaultDepthWrite && df.StartsWith("LessEqual");
            if (!isClearOrNonDraw && !isStandardDraw)
            {
                if (!hasState) { json.Key("state").BeginObject(); hasState = true; }
                json.Key("depth").BeginObject();
                json.Key("write").Value(dw != 0);
                json.Key("func").Value(df);
                json.EndObject();
            }
        }

        var rasterObj = GetFieldObject(eventData, "m_RasterState");
        if (rasterObj != null)
        {
            int cull = GetFieldAsInt(rasterObj, "cullMode");
            int bias = GetFieldAsInt(rasterObj, "depthBias");
            float slopeBias = GetField<float>(rasterObj, "slopeScaledDepthBias");
            bool isDefault = cull == c_DefaultRasterCull && bias == 0 && slopeBias == 0f;
            if (!isDefault)
            {
                if (!hasState) { json.Key("state").BeginObject(); hasState = true; }
                json.Key("raster").BeginObject();
                if (cull != c_DefaultRasterCull)
                    json.Key("cull").Value(GetEnumString(rasterObj, "cullMode"));
                if (bias != 0) json.Key("depthBias").Value(bias);
                if (slopeBias != 0f) json.Key("slopeBias").Value(slopeBias);
                json.EndObject();
            }
        }

        var stencilObj = GetFieldObject(eventData, "m_StencilState");
        if (stencilObj != null)
        {
            bool enabled = GetField<bool>(stencilObj, "stencilEnable");
            if (enabled)
            {
                if (!hasState) { json.Key("state").BeginObject(); hasState = true; }
                json.Key("stencil").BeginObject();
                json.Key("funcFront").Value(GetEnumString(stencilObj, "stencilFuncFront"));
                json.Key("funcBack").Value(GetEnumString(stencilObj, "stencilFuncBack"));
                int stRef = GetField<int>(eventData, "m_StencilRef");
                if (stRef != 0) json.Key("ref").Value(stRef);
                json.EndObject();
            }
        }

        if (hasState) json.EndObject();
    }

    private void WriteCleanShaderInfo(JsonBuilder json, object eventData)
    {
        var shaderInfo = GetFieldObject(eventData, "m_ShaderInfo");
        if (shaderInfo == null) return;

        bool hasProps = false;

        var keywords = GetFieldObject(shaderInfo, "keywords") as Array;
        if (keywords != null && keywords.Length > 0)
        {
            if (!hasProps) { json.Key("props").BeginObject(); hasProps = true; }
            json.Key("keywords").BeginArray();
            foreach (var kw in keywords)
            {
                if (kw is string s && !string.IsNullOrEmpty(s))
                    json.Value(s);
            }
            json.EndArray();
        }

        var textures = GetFieldObject(shaderInfo, "textures") as Array;
        if (textures != null && textures.Length > 0)
        {
            if (!hasProps) { json.Key("props").BeginObject(); hasProps = true; }
            json.Key("textures").BeginArray();
            foreach (var tex in textures)
            {
                string texName = GetField<string>(tex, "name");
                string texFile = GetField<string>(tex, "textureName");
                if (string.IsNullOrEmpty(texName)) continue;
                json.BeginObject();
                json.Key("name").Value(texName);
                if (!string.IsNullOrEmpty(texFile))
                    json.Key("tex").Value(texFile);
                json.EndObject();
            }
            json.EndArray();
        }

        var vectors = GetFieldObject(shaderInfo, "vectors") as Array;
        if (vectors != null && vectors.Length > 0)
        {
            if (!hasProps) { json.Key("props").BeginObject(); hasProps = true; }
            json.Key("vectors").BeginArray();
            foreach (var vec in vectors)
            {
                string vName = GetField<string>(vec, "name");
                if (string.IsNullOrEmpty(vName)) continue;
                var val = GetFieldObject(vec, "value");
                if (val == null) continue;
                json.BeginObject();
                json.Key("name").Value(vName);
                json.Key("v").BeginArray();
                json.Value(GetField<float>(val, "x"));
                json.Value(GetField<float>(val, "y"));
                json.Value(GetField<float>(val, "z"));
                json.Value(GetField<float>(val, "w"));
                json.EndArray();
                json.EndObject();
            }
            json.EndArray();
        }

        var floats = GetFieldObject(shaderInfo, "floats") as Array;
        if (floats != null && floats.Length > 0)
        {
            if (!hasProps) { json.Key("props").BeginObject(); hasProps = true; }
            json.Key("floats").BeginArray();
            foreach (var f in floats)
            {
                string fName = GetField<string>(f, "name");
                if (string.IsNullOrEmpty(fName)) continue;
                float fVal = GetField<float>(f, "value");
                json.BeginObject();
                json.Key("name").Value(fName);
                json.Key("v").Value(fVal);
                json.EndObject();
            }
            json.EndArray();
        }

        var ints = GetFieldObject(shaderInfo, "ints") as Array;
        if (ints != null && ints.Length > 0)
        {
            if (!hasProps) { json.Key("props").BeginObject(); hasProps = true; }
            json.Key("ints").BeginArray();
            foreach (var iv in ints)
            {
                string iName = GetField<string>(iv, "name");
                if (string.IsNullOrEmpty(iName)) continue;
                int iVal = GetField<int>(iv, "value");
                json.BeginObject();
                json.Key("name").Value(iName);
                json.Key("v").Value(iVal);
                json.EndObject();
            }
            json.EndArray();
        }

        // matrices, buffers, cBuffers — INTENTIONALLY SKIPPED
        // matrices: #1 bloat source, no perf-analysis value
        // buffers/cBuffers: rarely actionable

        if (hasProps) json.EndObject();
    }

    private string GetEnumString(object obj, string fieldName)
    {
        if (obj == null) return "0";
        var field = CachedField(obj.GetType(), fieldName);
        if (field == null) return "0";
        try
        {
            var val = field.GetValue(obj);
            return val?.ToString() ?? "0";
        }
        catch (Exception e)
        {
            ReportIssue($"enum-read:{obj.GetType().FullName}.{fieldName}",
                $"Failed to read enum field {obj.GetType().Name}.{fieldName}: {e.GetType().Name}: {e.Message}");
            return "0";
        }
    }

    #endregion

    #region Statistics

    private void ResetStats()
    {
        m_TotalVertices = 0;
        m_TotalIndices = 0;
        m_DrawCallCount = 0;
        if (m_ShaderDrawCalls == null) m_ShaderDrawCalls = new Dictionary<string, int>();
        else m_ShaderDrawCalls.Clear();
        if (m_ShaderVertices == null) m_ShaderVertices = new Dictionary<string, long>();
        else m_ShaderVertices.Clear();
        if (m_BatchBreakCauses == null) m_BatchBreakCauses = new Dictionary<string, int>();
        else m_BatchBreakCauses.Clear();
        if (m_EventTypeCounts == null) m_EventTypeCounts = new Dictionary<string, int>();
        else m_EventTypeCounts.Clear();
        if (m_RTTimeline == null) m_RTTimeline = new List<RTSpan>();
        else m_RTTimeline.Clear();
        m_LastRTName = null;
    }

    private void CollectStats(object eventData, int eventIndex)
    {
        int vc = GetField<int>(eventData, "m_VertexCount");
        m_TotalVertices += vc;

        int ic = GetField<int>(eventData, "m_IndexCount");
        m_TotalIndices += ic;

        int dc = GetField<int>(eventData, "m_DrawCallCount");
        if (dc > 0) m_DrawCallCount += dc;
        else if (vc > 0) m_DrawCallCount++;

        string shaderName = GetShaderName(eventData);

        if (!string.IsNullOrEmpty(shaderName))
        {
            m_ShaderDrawCalls.TryGetValue(shaderName, out int sc);
            m_ShaderDrawCalls[shaderName] = sc + 1;

            m_ShaderVertices.TryGetValue(shaderName, out long sv);
            m_ShaderVertices[shaderName] = sv + vc;
        }

        int causeInt = GetField<int>(eventData, "m_BatchBreakCause");
        if (causeInt > 0 && m_BatchBreakCauseStrings != null && causeInt < m_BatchBreakCauseStrings.Length)
        {
            string cause = m_BatchBreakCauseStrings[causeInt];
            if (!string.IsNullOrEmpty(cause))
            {
                m_BatchBreakCauses.TryGetValue(cause, out int bc);
                m_BatchBreakCauses[cause] = bc + 1;
            }
        }

        // Track render target switches
        string rtName = GetField<string>(eventData, "m_RenderTargetName");
        if (!string.IsNullOrEmpty(rtName) && rtName != m_LastRTName)
        {
            int rtW = GetField<int>(eventData, "m_RenderTargetWidth");
            int rtH = GetField<int>(eventData, "m_RenderTargetHeight");

            if (m_RTTimeline.Count > 0)
            {
                var last = m_RTTimeline[m_RTTimeline.Count - 1];
                last.endIndex = eventIndex - 1;
                m_RTTimeline[m_RTTimeline.Count - 1] = last;
            }

            m_RTTimeline.Add(new RTSpan
            {
                name = rtName, width = rtW, height = rtH,
                startIndex = eventIndex
            });
            m_LastRTName = rtName;
        }
    }

    private void WriteSummary(JsonBuilder json)
    {
        json.Key("summary").BeginObject();

        json.Key("totalVertices").Value(m_TotalVertices);
        json.Key("totalIndices").Value(m_TotalIndices);
        json.Key("totalTriangles").Value(m_TotalIndices / 3);
        json.Key("drawCalls").Value(m_DrawCallCount);
        json.Key("uniqueShaders").Value(m_ShaderDrawCalls.Count);

        // Shader distribution
        json.Key("shaderDistribution").BeginArray();
        foreach (var kvp in SortedDescending(m_ShaderDrawCalls))
        {
            json.BeginObject();
            json.Key("shader").Value(kvp.Key);
            json.Key("drawCalls").Value(kvp.Value);
            m_ShaderVertices.TryGetValue(kvp.Key, out long sv);
            json.Key("vertices").Value(sv);
            json.EndObject();
        }
        json.EndArray();

        // Batch break causes
        if (m_BatchBreakCauses.Count > 0)
            WriteSortedDistribution(json, "batchBreakCauses", "cause", m_BatchBreakCauses);

        // Event type distribution
        if (m_EventTypeCounts.Count > 0)
            WriteSortedDistribution(json, "eventTypeDistribution", "type", m_EventTypeCounts);

        // Render target timeline
        if (m_RTTimeline != null && m_RTTimeline.Count > 0)
        {
            var last = m_RTTimeline[m_RTTimeline.Count - 1];
            last.endIndex = m_TotalEventCount > 0 ? m_TotalEventCount - 1 : last.startIndex;
            m_RTTimeline[m_RTTimeline.Count - 1] = last;

            json.Key("renderTargets").BeginArray();
            foreach (var rt in m_RTTimeline)
            {
                json.BeginObject();
                json.Key("name").Value(rt.name);
                json.Key("size").BeginArray().Value(rt.width).Value(rt.height).EndArray();
                json.Key("events").BeginArray().Value(rt.startIndex).Value(rt.endIndex).EndArray();
                json.EndObject();
            }
            json.EndArray();
        }

        json.EndObject();
    }

    #endregion

    #region Helpers

    private static List<KeyValuePair<string, int>> SortedDescending(Dictionary<string, int> dict)
    {
        var list = new List<KeyValuePair<string, int>>(dict);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        return list;
    }

    private void WriteSortedDistribution(JsonBuilder json, string arrayKey, string itemKey,
        Dictionary<string, int> dict)
    {
        json.Key(arrayKey).BeginArray();
        foreach (var kvp in SortedDescending(dict))
        {
            json.BeginObject();
            json.Key(itemKey).Value(kvp.Key);
            json.Key("count").Value(kvp.Value);
            json.EndObject();
        }
        json.EndArray();
    }

    private FieldInfo CachedField(Type type, string name)
    {
        var key = (type, name);
        if (!m_FieldCache.TryGetValue(key, out var fi))
        {
            fi = type.GetField(name, s_Instance);
            // Unity internal structs use m_ prefix (e.g., m_SrcBlend, m_WriteMask).
            // Try with m_ prefix if direct lookup fails.
            if (fi == null && name.Length > 0 && !name.StartsWith("m_"))
            {
                string prefixed = "m_" + char.ToUpperInvariant(name[0]) + name.Substring(1);
                fi = type.GetField(prefixed, s_Instance);
            }
            m_FieldCache[key] = fi;
        }
        return fi;
    }

    private T GetField<T>(object obj, string fieldName)
    {
        if (obj == null) return default;
        var field = CachedField(obj.GetType(), fieldName);
        if (field != null)
        {
            try { return (T)field.GetValue(obj); }
            catch (Exception e)
            {
                ReportIssue($"field-read:{obj.GetType().FullName}.{fieldName}:{typeof(T).FullName}",
                    $"Failed to read field {obj.GetType().Name}.{fieldName} as {typeof(T).Name}: {e.GetType().Name}: {e.Message}");
            }
        }
        return default;
    }

    private object GetFieldObject(object obj, string fieldName)
    {
        if (obj == null) return null;
        var field = CachedField(obj.GetType(), fieldName);
        if (field == null) return null;
        try { return field.GetValue(obj); }
        catch (Exception e)
        {
            ReportIssue($"field-read:{obj.GetType().FullName}.{fieldName}:object",
                $"Failed to read field {obj.GetType().Name}.{fieldName}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read a field as int, safely handling enum types.
    /// GetField&lt;int&gt; fails on enums because C# unboxing requires exact type match.
    /// </summary>
    private int GetFieldAsInt(object obj, string fieldName)
    {
        if (obj == null) return 0;
        var field = CachedField(obj.GetType(), fieldName);
        if (field == null) return 0;
        try { return Convert.ToInt32(field.GetValue(obj), CultureInfo.InvariantCulture); }
        catch (Exception e)
        {
            ReportIssue($"field-read:{obj.GetType().FullName}.{fieldName}:int",
                $"Failed to convert field {obj.GetType().Name}.{fieldName} to int: {e.GetType().Name}: {e.Message}");
            return 0;
        }
    }

    private string GetShaderName(object eventData)
    {
        string shader = GetField<string>(eventData, "m_RealShaderName");
        if (string.IsNullOrEmpty(shader))
            shader = GetField<string>(eventData, "m_OriginalShaderName");
        return shader;
    }

    private static string ShortenEventName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        int execIdx = name.LastIndexOf("Execute: ", StringComparison.Ordinal);
        if (execIdx >= 0)
        {
            string afterExec = name.Substring(execIdx + 9);
            afterExec = afterExec.Replace("/RenderLoop.", "/");
            return afterExec;
        }

        int lastSlash = name.LastIndexOf('/');
        if (lastSlash > 0 && lastSlash < name.Length - 1)
            return name.Substring(lastSlash + 1);

        return name;
    }

    private static readonly List<string> s_PathParts = new List<string>(16);

    private static string GetTransformPath(Transform t)
    {
        if (t == null) return "";
        s_PathParts.Clear();
        var cur = t;
        while (cur != null) { s_PathParts.Add(cur.name); cur = cur.parent; }
        var sb = new StringBuilder(64);
        for (int i = s_PathParts.Count - 1; i >= 0; i--)
        {
            if (sb.Length > 0) sb.Append('/');
            sb.Append(s_PathParts[i]);
        }
        return sb.ToString();
    }

    #endregion

    #region JsonBuilder

    /// <summary>
    /// Lightweight JSON builder with pretty-print. No external dependencies.
    /// </summary>
    private class JsonBuilder
    {
        private readonly StringBuilder m_Sb;
        private int m_Indent;
        private bool m_NeedComma;
        private readonly Stack<bool> m_Stack = new Stack<bool>();

        public JsonBuilder(StringBuilder sb) { m_Sb = sb; }

        public JsonBuilder BeginObject()
        {
            PrepareValue();
            m_Stack.Push(m_NeedComma);
            m_NeedComma = false;
            m_Sb.Append('{');
            m_Indent++;
            return this;
        }

        public JsonBuilder EndObject()
        {
            m_Indent--;
            if (m_NeedComma) { m_Sb.Append('\n'); WriteIndent(); }
            m_Sb.Append('}');
            if (m_Stack.Count > 0) m_Stack.Pop();
            m_NeedComma = true;
            return this;
        }

        public JsonBuilder BeginArray()
        {
            PrepareValue();
            m_Stack.Push(m_NeedComma);
            m_NeedComma = false;
            m_Sb.Append('[');
            m_Indent++;
            return this;
        }

        public JsonBuilder EndArray()
        {
            m_Indent--;
            if (m_NeedComma) { m_Sb.Append('\n'); WriteIndent(); }
            m_Sb.Append(']');
            if (m_Stack.Count > 0) m_Stack.Pop();
            m_NeedComma = true;
            return this;
        }

        public JsonBuilder Key(string name)
        {
            if (m_NeedComma) m_Sb.Append(',');
            m_Sb.Append('\n');
            WriteIndent();
            WriteEscapedString(name);
            m_Sb.Append(": ");
            m_NeedComma = false;
            return this;
        }

        public JsonBuilder Value(string val)
        {
            PrepareValue();
            if (val == null) m_Sb.Append("null");
            else WriteEscapedString(val);
            m_NeedComma = true;
            return this;
        }

        public JsonBuilder Value(int val) { PrepareValue(); m_Sb.Append(val); m_NeedComma = true; return this; }
        public JsonBuilder Value(long val) { PrepareValue(); m_Sb.Append(val); m_NeedComma = true; return this; }

        public JsonBuilder Value(float val)
        {
            PrepareValue();
            if (float.IsNaN(val)) m_Sb.Append("\"NaN\"");
            else if (float.IsInfinity(val)) m_Sb.Append(val > 0 ? "\"Inf\"" : "\"-Inf\"");
            else m_Sb.Append(val.ToString("G9", CultureInfo.InvariantCulture));
            m_NeedComma = true;
            return this;
        }

        public JsonBuilder Value(double val)
        {
            PrepareValue();
            if (double.IsNaN(val)) m_Sb.Append("\"NaN\"");
            else if (double.IsInfinity(val)) m_Sb.Append(val > 0 ? "\"Inf\"" : "\"-Inf\"");
            else m_Sb.Append(val.ToString("G17", CultureInfo.InvariantCulture));
            m_NeedComma = true;
            return this;
        }

        public JsonBuilder Value(bool val) { PrepareValue(); m_Sb.Append(val ? "true" : "false"); m_NeedComma = true; return this; }
        public JsonBuilder Null() { PrepareValue(); m_Sb.Append("null"); m_NeedComma = true; return this; }

        private void PrepareValue()
        {
            if (!m_NeedComma) return;
            m_Sb.Append(',');
            m_Sb.Append('\n');
            WriteIndent();
        }

        private void WriteIndent()
        {
            for (int i = 0; i < m_Indent; i++) m_Sb.Append("  ");
        }

        private void WriteEscapedString(string s)
        {
            m_Sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  m_Sb.Append("\\\""); break;
                    case '\\': m_Sb.Append("\\\\"); break;
                    case '\n': m_Sb.Append("\\n"); break;
                    case '\r': m_Sb.Append("\\r"); break;
                    case '\t': m_Sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) m_Sb.AppendFormat("\\u{0:X4}", (int)c);
                        else m_Sb.Append(c);
                        break;
                }
            }
            m_Sb.Append('"');
        }
    }

    #endregion
}
