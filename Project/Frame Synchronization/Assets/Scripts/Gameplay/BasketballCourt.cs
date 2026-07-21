using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 球场渲染 — 用 LineRenderer 程序化绘制半场街篮球场线
    /// 包括：边界线／中线／罚球线／三分线弧／合理冲撞区／篮筐标记
    /// 参考街篮2 CourtRenderer + CourtConstant
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class BasketballCourt : MonoBehaviour
    {
        [Header("线条颜色")]
        [SerializeField] private Color _lineColor = Color.white;
        [SerializeField] private float _lineWidth = 0.05f;
        [SerializeField] private Material _lineMaterial;

        [Header("辅助对象")]
        [SerializeField] private GameObject _hoopPrefab; // 篮筐小球体

        private void Start()
        {
            DrawCourt();
            CreateHoop();
        }

        public void DrawCourt()
        {
            float halfW = CourtConstant.CourtHalfWidth.ToFloat();   // 5
            float halfD = CourtConstant.CourtHalfDepth.ToFloat();   // 4
            float totalD = halfD * 2;                               // 8

            // ① 边界线 — 半场矩形四条边（Z轴正向为底线，Z=0中线）
            DrawLineRect("CourtBoundary",
                new Vector3(-halfW, 0.02f, totalD),   // 左上 (底线)
                new Vector3(halfW, 0.02f, totalD),    // 右上 (底线)
                new Vector3(halfW, 0.02f, 0),         // 右下 (中线)
                new Vector3(-halfW, 0.02f, 0)         // 左下 (中线)
            );

            // ② 中线 — 半场分割线
            DrawLine("MidLine",
                new Vector3(-halfW, 0.02f, 0),
                new Vector3(halfW, 0.02f, 0));

            // ③ 罚球线 — 距离底线约2.5m
            float ftZ = CourtConstant.FreeThrowLineZ.ToFloat();     // 2.5
            float ftW = CourtConstant.FreeThrowLineWidth.ToFloat(); // 2.4
            DrawLine("FreeThrowLine",
                new Vector3(-ftW, 0.02f, totalD - ftZ),
                new Vector3(ftW, 0.02f, totalD - ftZ));

            // ④ 三分线弧 — 以篮筐为圆心，半径4.5m的半圆弧（约24线段）
            float hoopX = 0;
            float hoopZ = totalD - CourtConstant.HoopZ.ToFloat();   // 8 - 3.87 = 4.13
            float threeR = CourtConstant.ThreePointRadius.ToFloat(); // 4.5
            int segments = 24;
            Vector3[] arcPoints = new Vector3[segments + 1];

            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float angle = t * Mathf.PI; // 半圆弧 0~π
                float x = hoopX + threeR * Mathf.Cos(angle);
                float z = hoopZ + threeR * Mathf.Sin(angle);
                arcPoints[i] = new Vector3(x, 0.02f, z);
            }
            DrawPolyLine("ThreePointArc", arcPoints);

            // ⑤ 篮筐下方矩形标记 (合理冲撞区简化)
            float paintW = 1.2f;  // 宽
            float paintH = 1.5f;  // 高
            float paintZ = totalD - 3.5f;
            DrawLineRect("Paint",
                new Vector3(-paintW, 0.02f, paintZ + paintH),
                new Vector3(paintW, 0.02f, paintZ + paintH),
                new Vector3(paintW, 0.02f, paintZ),
                new Vector3(-paintW, 0.02f, paintZ)
            );
        }

        private void CreateHoop()
        {
            // 创建篮筐小球体
            var hoopObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hoopObj.name = "HoopVisual";
            hoopObj.transform.SetParent(transform);
            float hoopZ = CourtConstant.HoopZ.ToFloat();
            float totalD = CourtConstant.CourtHalfDepth.ToFloat() * 2;
            hoopObj.transform.position = new Vector3(0, CourtConstant.HoopY.ToFloat(), totalD - hoopZ);
            hoopObj.transform.localScale = new Vector3(0.15f, 0.15f, 0.15f);
            var renderer = hoopObj.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.material.color = new Color(0.0f, 0.0f, 1.0f);
            }
        }

        // ----- 辅助方法 -----
        private void DrawLine(string name, Vector3 start, Vector3 end)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform);
            var lr = go.AddComponent<LineRenderer>();
            ConfigureLine(lr);
            lr.positionCount = 2;
            lr.SetPosition(0, start);
            lr.SetPosition(1, end);
        }

        private void DrawLineRect(string name, params Vector3[] corners)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform);
            var lr = go.AddComponent<LineRenderer>();
            ConfigureLine(lr);
            lr.positionCount = 5;
            lr.loop = true;
            for (int i = 0; i < 4; i++)
                lr.SetPosition(i, corners[i]);
            lr.SetPosition(4, corners[0]);
        }

        private void DrawPolyLine(string name, Vector3[] points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform);
            var lr = go.AddComponent<LineRenderer>();
            ConfigureLine(lr);
            lr.positionCount = points.Length;
            for (int i = 0; i < points.Length; i++)
                lr.SetPosition(i, points[i]);
        }

        private void ConfigureLine(LineRenderer lr)
        {
            lr.startWidth = _lineWidth;
            lr.endWidth = _lineWidth;
            lr.startColor = _lineColor;
            lr.endColor = _lineColor;
            // 使用内置材质 — Unity 新建的 LineRenderer 默认有材质
            lr.material = new Material(Shader.Find("Hidden/Internal-Colored"));
            if (lr.material == null)
                lr.sharedMaterial = null; // 让 LineRenderer 使用默认材质
        }
    }
}
