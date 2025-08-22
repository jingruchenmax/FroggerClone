using System.Runtime.InteropServices;
using UnityEngine;

public static class WebGLBridge
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void SendGameEventMessage(string json);
#endif

    public static void PostJSON(string json)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        SendGameEventMessage(json);
#else
        Debug.Log($"[WebGLBridge] (Editor/Non-WebGL) {json}");
#endif
    }
}
