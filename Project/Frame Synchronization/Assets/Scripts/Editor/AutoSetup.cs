// using UnityEngine;
// using UnityEngine.UI;
// using UnityEditor;
//
// namespace FrameSyncDemo
// {
//     /// <summary>
//     /// 一键自动搭建场景 — 菜单: Tools → 帧同步Demo → 自动搭建场景
//     /// </summary>
//     public class AutoSetup : EditorWindow
//     {
//         [MenuItem("Tools/帧同步Demo/自动搭建场景")]
//         public static void SetupAll()
//         {
//             CleanupOldObjects();
//
//             // 1. FrameEngine
//             var engineGo = new GameObject("FrameEngine");
//             var engine = engineGo.AddComponent<FrameEngine>();
//
//             // 2. GameController
//             var controllerGo = new GameObject("GameController");
//             var controller = controllerGo.AddComponent<GameController>();
//             SetPrivateField(controller, "_frameEngine", engine);
//
//             // 3. Camera
//             if (Camera.main == null)
//             {
//                 var camGo = new GameObject("Main Camera");
//                 camGo.AddComponent<Camera>();
//                 camGo.transform.position = new Vector3(0, 10, -10);
//                 camGo.transform.LookAt(Vector3.zero);
//                 camGo.tag = "MainCamera";
//             }
//
//             // 4. Light
//             if (FindObjectOfType<Light>() == null)
//             {
//                 var lightGo = new GameObject("Directional Light");
//                 var light = lightGo.AddComponent<Light>();
//                 light.type = LightType.Directional;
//                 light.intensity = 1f;
//                 lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
//             }
//
//             // 5. Canvas + FrameDebugPanel (自动查找ByName)
//             var canvasGo = new GameObject("DebugCanvas");
//             var canvas = canvasGo.AddComponent<Canvas>();
//             canvas.renderMode = RenderMode.ScreenSpaceOverlay;
//             canvas.pixelPerfect = true;
//             var scaler = canvasGo.AddComponent<CanvasScaler>();
//             scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
//             scaler.referenceResolution = new Vector2(1920, 1080);
//             canvasGo.AddComponent<GraphicRaycaster>();
//             canvasGo.AddComponent<FrameDebugPanel>();
//
//             // --- 折叠条 ---
//             var collapsedBar = CreateUI("Panel_CollapsedBar", canvasGo.transform);
//             var cBarImg = collapsedBar.AddComponent<Image>();
//             cBarImg.color = new Color(0, 0, 0, 0.75f);
//             var cBarBtn = collapsedBar.AddComponent<Button>();
//             cBarBtn.transition = Selectable.Transition.None;
//             SetupHLG(collapsedBar, 12, 12);
//             SetAnchorTop(collapsedBar.GetComponent<RectTransform>(), 22);
//
//             CreateText("Text_CollapsedFrame", collapsedBar.transform, "帧#0", 12,
//                 new Color(0.38f, 0.65f, 1f), 55).fontStyle = FontStyle.Bold;
//
//             var lightGo2 = CreateUI("Image_StatusLight", collapsedBar.transform);
//             lightGo2.AddComponent<Image>().color = Color.green;
//             var le = lightGo2.AddComponent<LayoutElement>();
//             le.preferredWidth = 8; le.preferredHeight = 8;
//
//             CreateText("Text_CollapsedStatus", collapsedBar.transform, "●运行", 12,
//                 new Color(0.58f, 0.64f, 0.72f), 100);
//
//             // spacer
//             var spacer = CreateUI("Spacer", collapsedBar.transform);
//             spacer.AddComponent<LayoutElement>().flexibleWidth = 1;
//
//             // --- 展开面板 (初始隐藏) ---
//             var expandedPanel = CreateUI("Panel_Expanded", canvasGo.transform);
//             expandedPanel.AddComponent<Image>().color = new Color(0, 0, 0, 0.85f);
//             SetupHLG(expandedPanel, 8, 12);
//             var ert = expandedPanel.GetComponent<RectTransform>();
//             ert.anchorMin = new Vector2(0, 1);
//             ert.anchorMax = new Vector2(1, 1);
//             ert.pivot = new Vector2(0.5f, 1);
//             ert.sizeDelta = new Vector2(0, 36);
//             ert.anchoredPosition = new Vector2(0, -22);
//             expandedPanel.SetActive(false);
//
//             CreateText("Text_ExpandedFrame", expandedPanel.transform, "帧#0 (30FPS)", 11,
//                 new Color(0.38f, 0.65f, 1f), 85);
//             CreateText("Text_P1Input", expandedPanel.transform, "P1: dir:0 btn:0 raw:0", 10,
//                 new Color(0.9f, 0.2f, 0.2f), 200);
//             CreateText("Text_P2Input", expandedPanel.transform, "P2: dir:0 btn:0 raw:0", 10,
//                 new Color(0.2f, 0.4f, 0.9f), 200);
//             CreateText("Text_P1Pos", expandedPanel.transform, "(-3,0)", 11,
//                 new Color(0.9f, 0.2f, 0.2f), 80).fontStyle = FontStyle.Bold;
//             CreateText("Text_P2Pos", expandedPanel.transform, "(3,0)", 11,
//                 new Color(0.2f, 0.4f, 0.9f), 80).fontStyle = FontStyle.Bold;
//             CreateText("Text_Buffer", expandedPanel.transform, "缓冲:0/256帧", 10,
//                 new Color(0.58f, 0.64f, 0.72f), 95);
//             CreateText("Text_Info", expandedPanel.transform, "运行:0s 追:0", 10,
//                 new Color(0.4f, 0.4f, 0.4f), 100);
//             CreateText("Text_Recording", expandedPanel.transform, "待机", 10,
//                 new Color(0.9f, 0.2f, 0.2f), 150);
//
//             Debug.Log("✅ 帧同步Demo场景已搭建！\n" +
//                       "  Play → WASD红方 / 方向键蓝方\n" +
//                       "  Space暂停 R录制 P回放 C清空\n" +
//                       "  Window → 帧同步调试器");
//             Selection.activeGameObject = controllerGo;
//         }
//
//         private static void CleanupOldObjects()
//         {
//             foreach (var name in new[] { "GameController", "FrameEngine", "DebugCanvas", "Main Camera", "Directional Light" })
//             {
//                 var go = GameObject.Find(name);
//                 if (go != null) Object.DestroyImmediate(go);
//             }
//         }
//
//         private static GameObject CreateUI(string name, Transform parent)
//         {
//             var go = new GameObject(name);
//             go.transform.SetParent(parent, false);
//             return go;
//         }
//
//         private static Text CreateText(string name, Transform parent, string text, int fontSize, Color color, float width)
//         {
//             var go = CreateUI(name, parent);
//             var t = go.AddComponent<Text>();
//             t.text = text;
//             t.fontSize = fontSize;
//             t.color = color;
//             t.alignment = TextAnchor.MiddleLeft;
//             t.raycastTarget = false;
//             t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
//             var le = go.AddComponent<LayoutElement>();
//             le.preferredWidth = width;
//             return t;
//         }
//
//         private static void SetupHLG(GameObject go, float spacing, float padX)
//         {
//             var hlg = go.AddComponent<HorizontalLayoutGroup>();
//             hlg.childAlignment = TextAnchor.MiddleLeft;
//             hlg.spacing = spacing;
//             hlg.padding = new RectOffset(padX, padX, 0, 0);
//             hlg.childControlWidth = false;
//             hlg.childControlHeight = true;
//             hlg.childForceExpandWidth = false;
//             hlg.childForceExpandHeight = true;
//         }
//
//         private static void SetAnchorTop(RectTransform rt, float height)
//         {
//             rt.anchorMin = new Vector2(0, 1);
//             rt.anchorMax = new Vector2(1, 1);
//             rt.pivot = new Vector2(0.5f, 1);
//             rt.sizeDelta = new Vector2(0, height);
//             rt.anchoredPosition = Vector2.zero;
//         }
//
//         private static void SetPrivateField(Object obj, string name, Object value)
//         {
//             var f = obj.GetType().GetField(name,
//                 System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
//             if (f != null) f.SetValue(obj, value);
//         }
//     }
// }
