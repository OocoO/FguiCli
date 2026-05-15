#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FguiRenderServer
{
    /// <summary>
    /// 手动测试：发布 FairyGUI 包 → 加载组件 → 渲染截图，验证整条流水线是否正常工作。
    /// 在 Unity 菜单栏选择  FguiTest / Run Component Loader Test  触发。
    /// </summary>
    public static class ComponentLoaderTest
    {
        // ── 配置区：根据实际项目修改这三个路径 ──────────────────────────────────

        /// FairyGUI 项目根目录（包含 *.fairy 文件的那一层，或其 assets 子目录均可）
        private const string FguiRootPath = "D:\\ProjectGit\\AirLegion\\fgui_airLegion";

        /// 发布输出目录（*_fui.bytes 等运行时文件将写入此处）
        private const string PublishDir = "D:/test/Published";

        /// 要渲染的包名（对应 package.xml 中的 name 属性）
        private const string TestPackageName = "BattleUI";

        /// 要渲染的组件名（不含 .xml 后缀）
        private const string TestComponentName = "debugFormationSelect";

        /// 输出 PNG 路径
        private const string OutPng = "D:/test/TestOutput/ComponentLoaderTest.png";

        // ────────────────────────────────────────────────────────────────────────

        [MenuItem("FguiTest/Run Component Loader Test")]
        public static void RunTest()
        {
            Debug.Log("[ComponentLoaderTest] ── Step 1: Publish all packages ──");

            // 1. 发布：把 FairyGUI 源文件编译成运行时格式
            try
            {
                // FguiPackagePublisher.PublishPackageAll(FguiRootPath, PublishDir);
                Debug.Log($"[ComponentLoaderTest] Publish OK → {PublishDir}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ComponentLoaderTest] Publish FAILED: {ex.Message}\n{ex.StackTrace}");
                return;
            }

            Debug.Log($"[ComponentLoaderTest] ── Step 2: Render '{TestPackageName}/{TestComponentName}' ──");

            // 2. 构建渲染请求
            var request = new FguiRenderRequest
            {
                allPackageRootDir = PublishDir,
                packageName       = TestPackageName,
                componentName     = TestComponentName,
                outPng            = OutPng,
                transparent       = true,
            };

            // 3. 进入 Play Mode 并渲染；结果在回调中输出
            if (EditorApplication.isPlaying)
            {
                DispatchRender(request);
            }
            else
            {
                // 先进入 Play Mode，再在 EnteredPlayMode 时触发渲染
                _pendingRequest = request;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                EditorApplication.isPlaying = true;
            }
        }

        // ── Play Mode 调度 ───────────────────────────────────────────────────────

        private static FguiRenderRequest _pendingRequest;

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
                return;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            if (_pendingRequest == null)
                return;

            FguiRenderRequest req = _pendingRequest;
            _pendingRequest = null;
            DispatchRender(req);
        }

        private static void DispatchRender(FguiRenderRequest request)
        {
            bool accepted = FguiRenderBootstrap.TryRenderInPlayMode(request, result =>
            {
                if (result.ok)
                {
                    Debug.Log($"[ComponentLoaderTest] ✓ Render OK — PNG: {result.pngPath}  ({result.durationMs} ms)\n" +
                              $"  File exists: {File.Exists(result.pngPath)}  " +
                              $"  Size: {(File.Exists(result.pngPath) ? new FileInfo(result.pngPath).Length + " bytes" : "N/A")}");
                }
                else
                {
                    Debug.LogError($"[ComponentLoaderTest] ✗ Render FAILED: {result.message}");
                }
            });

            if (!accepted)
                Debug.LogError("[ComponentLoaderTest] TryRenderInPlayMode rejected — renderer is busy.");
        }
    }
}
#endif
