using System.Runtime.InteropServices;
using UnityEngine;

public static class WebGLBridge
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void SendGameEventMessage(string type, string json);
#endif

    public static void PostJSON(string type, string json)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        SendGameEventMessage(type,json);
#else
        Debug.Log($"[WebGLBridge] (Editor/Non-WebGL) {json}");
#endif
    }
}
