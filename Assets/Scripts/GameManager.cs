using UnityEngine;
using UnityEngine.SceneManagement;
using System;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Critical Settings")]
    [Tooltip("If true, players use rolling (juicy) movement; if false, instant grid jump + visuals off.")]
    public bool isJuicy = true;

    [Tooltip("Global difficulty multiplier for spawners (0.5 .. 1.5).")]
    [Range(0.5f, 1.5f)] public float globalDifficulty = 1.0f;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

#if UNITY_WEBGL && !UNITY_EDITOR
        ApplyWebGLUrlSettings();
#endif

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        AlignLevelSettings();
    }

    /// <summary>
    /// Apply difficulty & juicy based on URL query params for WebGL.
    /// Example:  index.html?difficultyLevel=3&isJuicy=0
    /// </summary>
    void ApplyWebGLUrlSettings()
    {
        try
        {
            var url = Application.absoluteURL;
            if (string.IsNullOrEmpty(url)) return;

            Uri uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

            // difficultyLevel ? {1..5}
            string diffStr = query.Get("difficultyLevel");
            if (!string.IsNullOrEmpty(diffStr) && int.TryParse(diffStr, out int diffLevel))
            {
                diffLevel = Mathf.Clamp(diffLevel, 1, 5);
                // Map 1..5 to 0.5..1.5
                globalDifficulty = Mathf.Lerp(0.5f, 1.5f, (diffLevel - 1) / 4f);
            }

            // isJuicy ? {0,1}
            string juicyStr = query.Get("isJuicy");
            if (!string.IsNullOrEmpty(juicyStr) && int.TryParse(juicyStr, out int j))
            {
                isJuicy = (j != 0);
            }

            Debug.Log($"[GameManager] URL settings applied ? globalDifficulty={globalDifficulty}, isJuicy={isJuicy}");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[GameManager] Failed to parse WebGL URL params: " + e);
        }
    }

    /// <summary>
    /// Re-apply juicy & difficulty across the active scene.
    /// Call this after restarting a level or changing settings at runtime.
    /// </summary>
    public void AlignLevelSettings()
    {
        // 1) Difficulty ? spawners
        float clamped = Mathf.Clamp(globalDifficulty, 0.5f, 1.5f);
        ObjectSpawner.GlobalDifficulty = clamped;

        var spawners = FindObjectsOfType<ObjectSpawner>(includeInactive: true);
        foreach (var sp in spawners)
        {
            if (sp == null) continue;
            sp.localDifficulty = clamped;
            sp.ForceApplyJuicyTemplate(isJuicy);
        }

        // 2) Players ? visuals
        var players = FindObjectsOfType<PlayerController>(includeInactive: true);
        foreach (var p in players)
        {
            if (p == null) continue;
            p.juicy = isJuicy;

            foreach (var anim in p.GetComponentsInChildren<Animator>(true))
                if (anim != null) anim.enabled = isJuicy;

            foreach (var ps in p.GetComponentsInChildren<ParticleSystem>(true))
                if (ps != null && ps.gameObject != null)
                    ps.gameObject.SetActive(isJuicy);
        }

        // 3) Finish points ? visuals
        var points = FindObjectsOfType<CompletionPointController>(includeInactive: true);
        foreach (var cpc in points)
        {
            if (cpc == null) continue;
            cpc.isJuicy = isJuicy;

            foreach (var ps in cpc.GetComponentsInChildren<ParticleSystem>(true))
                if (ps != null && ps.gameObject != null)
                    ps.gameObject.SetActive(isJuicy);
        }
    }

    // Optional: hook up to UI sliders/toggles
    public void SetJuicy(bool value)
    {
        isJuicy = value;
        AlignLevelSettings();
    }

    public void SetGlobalDifficulty(float value)
    {
        globalDifficulty = Mathf.Clamp(value, 0.5f, 1.5f);
        AlignLevelSettings();
    }
}
