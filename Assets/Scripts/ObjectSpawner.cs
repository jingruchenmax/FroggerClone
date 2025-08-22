using UnityEngine;

public class ObjectSpawner : MonoBehaviour
{
    // ====== Global Difficulty ======
    public static float GlobalDifficulty = 1f;

    [Header("Difficulty")]
    [Tooltip("If true, use ObjectSpawner.GlobalDifficulty; otherwise use Local Difficulty below.")]
    public bool useGlobalDifficulty = true;

    [Tooltip("Local fallback when not using global. 0.5 (easy) .. 1.5 (hard).")]
    [Range(0.5f, 1.5f)] public float localDifficulty = 1f;

    [Header("Placeholders")]
    public Transform spawnPoint;
    public Transform targetPlaceholder;
    public Transform worldParent;

    [Header("Prefab & Timing")]
    public GameObject samplePrefab;
    [Min(0f)] public float initialDelay = 0f;
    [Min(0.05f)] public float spawnGapSeconds = 1.5f;
    [Tooltip("0 = unlimited concurrent spawns; otherwise cap alive objects.")]
    public int maxAlive = 0;

    [Header("Movement")]
    public Vector3 moveSpeed = new Vector3(2f, 0f, 0f);

    [Header("Optional")]
    public bool alignToPath = true;

    [Header("Preheat")]
    [Tooltip("Spawn as if the lane had already been running for this many seconds.")]
    [Min(0f)] public float preheatSeconds = 2.5f;
    [Tooltip("Enable/disable the preheat pass on enable/start.")]
    public bool enablePreheat = true;

    [Header("Juicy Visual Control")]
    [Tooltip("Animator state name used when non-juicy mode is active.")]
    public string noJuicyStateName = "No Juicy";
    [Tooltip("Optional animator state to play when juicy mode is active (leave empty to keep default).")]
    public string juicyStateName = "";

    int _alive;

    // cache last applied values so we can reschedule if difficulty changes
    float _lastAppliedDifficulty = -1f;
    float _lastAppliedGap = -1f;

    // ---------- Effective values with difficulty ----------
    float Difficulty => Mathf.Clamp(useGlobalDifficulty ? GlobalDifficulty : localDifficulty, 0.5f, 1.5f);

    // Shorter gaps when harder: effectiveGap = baseGap / difficulty
    float EffectiveGapSeconds => Mathf.Max(0.01f, spawnGapSeconds / Difficulty);

    // Faster movement when harder: effectiveSpeed = baseSpeed * difficulty
    Vector3 EffectiveMoveSpeed => moveSpeed * Difficulty;

    void Awake()
    {
        if (spawnPoint == null) spawnPoint = transform;
    }

    void OnEnable()
    {
        // Keep the template aligned with the current juicy setting (best-effort)
        ApplyJuicyToTemplate();
        ApplyDifficultyAndSchedule();
    }

    void OnDisable()
    {
        CancelInvoke(nameof(TrySpawn));
    }

    void Update()
    {
        // If difficulty changed at runtime, re-apply movement/gap and reschedule
        if (!Mathf.Approximately(_lastAppliedDifficulty, Difficulty) ||
            !Mathf.Approximately(_lastAppliedGap, EffectiveGapSeconds))
        {
            ApplyDifficultyAndSchedule();
        }

        // If juicy mode toggled at runtime (via GameManager), keep the template aligned
        // (Instances are always adjusted at spawn time.)
        ApplyJuicyToTemplate();
    }

    void ApplyDifficultyAndSchedule()
    {
        // Cancel any previous schedule
        CancelInvoke(nameof(TrySpawn));

        // Preheat first so the board isn't empty (uses effective values)
        if (enablePreheat && samplePrefab != null && spawnPoint != null)
        {
            RunPreheat();
        }

        // Then start normal cadence with effective gap
        InvokeRepeating(nameof(TrySpawn), initialDelay, EffectiveGapSeconds);

        _lastAppliedDifficulty = Difficulty;
        _lastAppliedGap = EffectiveGapSeconds;
    }

    void TrySpawn()
    {
        if (samplePrefab == null || spawnPoint == null) return;
        if (maxAlive > 0 && _alive >= maxAlive) return;

        SpawnOneWithTimeOffset(0f); // regular spawn (no back-time)
    }

    void RunPreheat()
    {
        if (preheatSeconds <= 0f) return;

        float gap = EffectiveGapSeconds;

        // How many would have spawned in the preheat window?
        int count = Mathf.FloorToInt(preheatSeconds / gap);

        // Oldest first so nearer ones don’t get blocked when using physics
        for (int i = count; i >= 1; i--)
        {
            if (maxAlive > 0 && _alive >= maxAlive) break;

            float backTime = i * gap;
            if (backTime > preheatSeconds) backTime = preheatSeconds;

            SpawnOneWithTimeOffset(backTime);
        }
    }

    void SpawnOneWithTimeOffset(float backTimeSeconds)
    {
        Transform parent = worldParent != null ? worldParent : spawnPoint.parent;

        // Instantiate as a child to preserve LOCAL TRS
        GameObject go = Instantiate(samplePrefab, parent, false);

        // Start with the spawn point's LOCAL transform
        go.transform.localPosition = spawnPoint.localPosition;
        go.transform.localRotation = spawnPoint.localRotation;
        go.transform.localScale = spawnPoint.localScale;

        // Optional: orient to target (compute in world, convert to local)
        if (alignToPath && targetPlaceholder != null && parent != null)
        {
            Vector3 worldFrom = parent.TransformPoint(go.transform.localPosition);
            Vector3 dir = (targetPlaceholder.position - worldFrom);
            if (dir.sqrMagnitude > 0.0001f)
            {
                Quaternion worldLook = Quaternion.LookRotation(dir.normalized, Vector3.up);
                go.transform.localRotation = Quaternion.Inverse(parent.rotation) * worldLook;
            }
        }

        // If preheating, push it backward along the motion path so it looks mid-stream
        if (backTimeSeconds > 0f)
        {
            // Convert world offset to local so we don’t break the hierarchy math
            Vector3 worldOffset = -EffectiveMoveSpeed * backTimeSeconds; // rewind along path
            Vector3 localOffset = parent != null
                ? parent.InverseTransformVector(worldOffset)
                : worldOffset; // no parent means local==world

            go.transform.localPosition += localOffset;
        }

        // Controller setup
        ObjectController ctrl = go.GetComponent<ObjectController>();
        if (ctrl == null) ctrl = go.AddComponent<ObjectController>();
        ctrl.Speed = EffectiveMoveSpeed;                // << apply difficulty
        ctrl.TargetPlaceholder = targetPlaceholder;

        // ---- Apply Juicy / Non-Juicy visual every time we spawn ----
        ApplyJuicyToInstance(go);

        ctrl.OnDespawn += HandleDespawn;
        _alive++;
    }

    void HandleDespawn(ObjectController controller)
    {
        _alive = Mathf.Max(0, _alive - 1);
        if (controller != null) controller.OnDespawn -= HandleDespawn;
    }

    [ContextMenu("Spawn Now")]
    public void SpawnNow() => TrySpawn();

    // ================= Juicy Helpers =================

    void ApplyJuicyToTemplate()
    {
        if (samplePrefab == null) return;

        bool juicy = true;
        if (GameManager.Instance != null) juicy = GameManager.Instance.isJuicy;

        var anim = samplePrefab.GetComponentInChildren<Animator>(true);
        if (anim == null) return;

        if (juicy)
        {
            // return control to animator
            anim.speed = 1f;
            if (!string.IsNullOrEmpty(juicyStateName))
            {
                try { anim.Play(juicyStateName, 0, 0f); anim.Update(0f); }
                catch { /* ignore */ }
            }
        }
        else
        {
            // lock into "No Juicy"
            try { if (!string.IsNullOrEmpty(noJuicyStateName)) { anim.Play(noJuicyStateName, 0, 0f); anim.Update(0f); } }
            catch { /* ignore invalid state */ }
            anim.speed = 0f; // hold pose
        }
    }

    void ApplyJuicyToInstance(GameObject go)
    {
        bool juicy = true;
        if (GameManager.Instance != null) juicy = GameManager.Instance.isJuicy;

        var anim = go != null ? go.GetComponentInChildren<Animator>(true) : null;
        if (anim == null) return;

        if (juicy)
        {
            anim.speed = 1f;
            if (!string.IsNullOrEmpty(juicyStateName))
            {
                try { anim.Play(juicyStateName, 0, 0f); }
                catch { /* ignore */ }
            }
        }
        else
        {
            try { if (!string.IsNullOrEmpty(noJuicyStateName)) anim.Play(noJuicyStateName, 0, 0f); }
            catch { /* ignore */ }
            anim.Update(0f);
            anim.speed = 0f; // freeze in non-juicy
        }
    }

    // Inside ObjectSpawner.cs
    public void ForceApplyJuicyTemplate(bool juicy)
    {
        // Internally reuse the same method that Update() calls
        ApplyJuicyToTemplate();
    }


#if UNITY_EDITOR
    void OnValidate()
    {
        // Keep values in sensible ranges in editor
        localDifficulty = Mathf.Clamp(localDifficulty, 0.5f, 1.5f);
        // In play mode, changes through inspector take effect immediately
        if (Application.isPlaying)
        {
            ApplyJuicyToTemplate();
            ApplyDifficultyAndSchedule();
        }
    }
#endif
}
