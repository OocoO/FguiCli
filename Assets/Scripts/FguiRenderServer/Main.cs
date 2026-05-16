namespace FguiRenderServer
{
    public static class Main
    {
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (UnityEngine.Object.FindObjectOfType<FguiRenderServerBehaviour>() != null)
            {
                return;
            }

            UnityEngine.GameObject gameObject = new UnityEngine.GameObject("FguiRenderServer");
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<FguiRenderServerBehaviour>();
        }
    }
}