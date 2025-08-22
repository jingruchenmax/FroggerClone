using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using UnityEngine.Events; // UnityAction

public class LevelManager : MonoBehaviour
{
    public static LevelManager Instance { get; private set; }

    [Header("Loop Settings")]
    public int totalLevelResets = 3;   // how many times to replay AFTER a level is completed
    public bool resetPointsOnStart = true;

    // Finish points
    readonly List<CompletionPointController> _points = new();
    readonly Dictionary<CompletionPointController, UnityAction> _pointHandlers = new();

    // Session / loop state
    int _doneCount;
    int _levelResetsLeft;              // separate from lives!
    double _levelStartTime;
    string _sceneName;

    // Scoring
    int _scoreCommitted;               // banked across finishes in THIS level session
    int _scoreRun;                     // progress since last respawn (cleared on death)

    // Global high score (persists while this manager lives)
    int _highScore;

    // Lives
    const int MAX_LIVES = 3;
    int _lives;
    int _deaths;

    // Logging
    readonly List<MovementEvent> _movementLog = new();
    readonly List<CollisionEvent> _collisionLog = new();

    // UI
    GameObject _completeTextGO;
    TextMeshProUGUI _scoreTextTMP;
    TextMeshProUGUI _highScoreTMP;
    TextMeshProUGUI _livesTMP;
    [SerializeField] private char livesGlyph = '\u25A0';  // Black Square U+25A0

    // Finalization guard per session
    bool _sessionFinalized;

    // Why are we (re)initializing the scene?
    enum ReloadReason { Startup, LivesReset, LevelCompleted }
    ReloadReason _pendingReloadReason = ReloadReason.Startup;

    // Public info
    public int ResetsCompleted => (totalLevelResets - _levelResetsLeft); // only counts level-complete cycles
    public double ElapsedSeconds => Time.realtimeSinceStartupAsDouble - _levelStartTime;

    // === NEW: Finish GameObject reference ===
    GameObject _finishGO;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _sceneName = SceneManager.GetActiveScene().name;
        _levelResetsLeft = totalLevelResets;

        SceneManager.sceneLoaded += OnSceneLoaded;

        PlayerController.OnPlayerMoved += HandlePlayerMoved;
        PlayerController.OnPlayerCollision += HandlePlayerCollision;
        PlayerController.OnPlayerDeath += HandlePlayerDeath;
        PlayerController.OnPlayerReachedFinish += HandlePlayerFinish;

        // First init is "Startup"
        _pendingReloadReason = ReloadReason.Startup;
        CacheFinishGameObject();
        InitLevel(_pendingReloadReason);
    }

    void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;

        PlayerController.OnPlayerMoved -= HandlePlayerMoved;
        PlayerController.OnPlayerCollision -= HandlePlayerCollision;
        PlayerController.OnPlayerDeath -= HandlePlayerDeath;
        PlayerController.OnPlayerReachedFinish -= HandlePlayerFinish;

        UnhookPointListeners();
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _sceneName = scene.name;
        CacheFinishGameObject();
        InitLevel(_pendingReloadReason);
        _pendingReloadReason = ReloadReason.Startup; // default after handling
    }

    // ========= FINISH GO cache =========
    void CacheFinishGameObject()
    {
        _finishGO = GameObject.FindGameObjectWithTag("Finish");
        if (_finishGO != null)
            _finishGO.SetActive(false); // Default to OFF at scene start
    }

    void SetFinishActive(bool state)
    {
        if (_finishGO == null)
            CacheFinishGameObject();
        if (_finishGO != null)
            _finishGO.SetActive(state);
    }
    // ===================================

    // ================== INIT ==================
    void InitLevel(ReloadReason reason)
    {
        _sessionFinalized = false;

        // Common fresh state for any reload
        _doneCount = 0;
        _levelStartTime = Time.realtimeSinceStartupAsDouble;
        _movementLog.Clear();
        _collisionLog.Clear();

        UnhookPointListeners();

        // UI
        _completeTextGO = GameObject.Find("CompleteText");
        if (_completeTextGO) _completeTextGO.SetActive(false);

        _scoreTextTMP = null;
        _highScoreTMP = null;
        _livesTMP = null;

        var scoreObj = GameObject.FindGameObjectWithTag("ScoreText");
        if (scoreObj) _scoreTextTMP = scoreObj.GetComponent<TextMeshProUGUI>();

        var hiObj = GameObject.FindGameObjectWithTag("HighScore");
        if (hiObj) _highScoreTMP = hiObj.GetComponent<TextMeshProUGUI>();

        var livesObj = GameObject.FindGameObjectWithTag("Lives");
        if (livesObj) _livesTMP = livesObj.GetComponent<TextMeshProUGUI>();

        // Rules by reason
        switch (reason)
        {
            case ReloadReason.Startup:
                _scoreCommitted = 0;
                _scoreRun = 0;
                _deaths = 0;
                _lives = MAX_LIVES;             // start with full lives
                break;

            case ReloadReason.LivesReset:
                _scoreRun = 0;                   // progress lost on wipe
                _lives = MAX_LIVES;              // refill lives
                // do not touch _levelResetsLeft
                break;

            case ReloadReason.LevelCompleted:
                _scoreCommitted = 0;
                _scoreRun = 0;
                _deaths = 0;
                _lives = MAX_LIVES;              // refill lives at the start of a new level cycle
                break;
        }

        UpdateScoreText();
        UpdateHighScoreText();
        UpdateLivesText();

        // Find points and (optionally) reset them
        _points.Clear();
        var found = GameObject.FindObjectsOfType<CompletionPointController>(includeInactive: false);
        if (found != null && found.Length > 0)
        {
            _points.AddRange(found);
            if (resetPointsOnStart)
                foreach (var p in _points) p.ResetPoint();

            foreach (var p in _points)
            {
                UnityAction h = () => HandlePointDone(p);
                p.OnDone.AddListener(h);
                _pointHandlers[p] = h;
            }
        }

        if (_points.Count == 0)
            HandleAllPointsDone(); // immediately complete if none

        // --- Make sure the finish object is hidden at the start ---
        SetFinishActive(false);
    }

    void UnhookPointListeners()
    {
        foreach (var kv in _pointHandlers)
        {
            var p = kv.Key;
            var h = kv.Value;
            if (p != null) p.OnDone.RemoveListener(h);
        }
        _pointHandlers.Clear();
    }

    // =============== FINISH-POINTS ===============
    void HandlePointDone(CompletionPointController point)
    {
        int newDone = 0;
        foreach (var p in _points)
            if (p != null && p.IsDone) newDone++;
        _doneCount = newDone;

        if (_doneCount >= _points.Count)
            HandleAllPointsDone();
    }

    void HandleAllPointsDone()
    {
        if (_sessionFinalized) return;
        _sessionFinalized = true;
        CompleteLevelAndUseReset();
    }

    // =============== PLAYER EVENTS ===============
    void HandlePlayerMoved(MovementEvent ev)
    {
        _movementLog.Add(ev);

        // +10 forward, -10 backward (clamped to >=0 so progress can't go negative)
        if (ev.direction == Vector3.forward)
            _scoreRun += 10;
        else if (ev.direction == Vector3.back)
            _scoreRun = Mathf.Max(0, _scoreRun - 10);

        UpdateScoreText();
        UpdateHighScoreIfNeeded();

        Debug.Log($"[Score] Run={_scoreRun}, Total={_scoreCommitted + _scoreRun}");
    }

    void HandlePlayerCollision(CollisionEvent ev)
    {
        _collisionLog.Add(ev);
        Debug.Log($"[Collision] {ev.phase} with '{ev.otherName}' (tag {ev.otherTag})");
    }

    void HandlePlayerDeath()
    {
        _deaths++;
        _lives = Mathf.Max(0, _lives - 1);
        UpdateLivesText();

        if (_scoreRun != 0)
        {
            Debug.Log($"[Score] Death: clearing run progress {_scoreRun}.");
            _scoreRun = 0;
            UpdateScoreText();
        }

        Debug.Log($"[Death] total deaths this session: #{_deaths} | Lives left: {_lives}");

        if (_lives <= 0 && !_sessionFinalized)
        {
            _sessionFinalized = true;
            CompleteLevelAndUseReset();
        }
    }

    void HandlePlayerFinish()
    {
        // Bank run progress + final bonus 140, then respawn
        int banked = _scoreRun + 140;
        _scoreCommitted += banked;
        _scoreRun = 0;

        Debug.Log($"[Score] Finish: +140 bonus, banked +{banked}. TotalCommitted={_scoreCommitted}");
        UpdateScoreText();
        UpdateHighScoreIfNeeded();

        // Respawn the player for the next run
        var player = FindObjectOfType<PlayerController>();
        if (player != null)
            player.gameObject.SendMessage("Respawn", SendMessageOptions.DontRequireReceiver);
    }

    // =============== RESET LOGIC (NEW CENTRALIZED) ===============
    void CompleteLevelAndUseReset()
    {
        WriteSessionJSON();

        if (_levelResetsLeft > 0)
        {
            _levelResetsLeft--;
            _pendingReloadReason = ReloadReason.LevelCompleted;
            StartCoroutine(ReloadSameSceneNextFrame());
        }
        else
        {
            SetFinishActive(true);
            if (_completeTextGO) _completeTextGO.SetActive(true);
            Debug.Log("[LevelManager] All resets consumed. Finish object revealed.");
            _sessionFinalized = true;
        }
    }

    IEnumerator ReloadSameSceneNextFrame()
    {
        yield return null;
        SceneManager.LoadScene(_sceneName, LoadSceneMode.Single);
    }

    // =============== JSON per LEVEL-COMPLETE only ===============
    void WriteSessionJSON()
    {
        int resetIndex = ResetsCompleted;        // counts ONLY level-complete loops
        double durationSeconds = ElapsedSeconds;
        int score = CurrentScore;                // committed + run (run should be 0 on completion)
        int deaths = CurrentDeaths;

        var movementList = new MovementEventList { items = _movementLog.ToArray() };
        var collisionList = new CollisionEventList { items = _collisionLog.ToArray() };
        var summary = new LevelSummary
        {
            level = _sceneName,
            resetIndex = resetIndex,
            durationSeconds = durationSeconds,
            deaths = deaths,
            score = score
        };

        string movementJson = JsonUtility.ToJson(movementList, true);
        string collisionsJson = JsonUtility.ToJson(collisionList, true);
        string summaryJson = JsonUtility.ToJson(summary, true);

        string dir = Application.persistentDataPath;
        SafeWrite(Path.Combine(dir, $"movement_{_sceneName}_{resetIndex}.json"), movementJson);
        SafeWrite(Path.Combine(dir, $"collisions_{_sceneName}_{resetIndex}.json"), collisionsJson);
        SafeWrite(Path.Combine(dir, $"summary_{_sceneName}_{resetIndex}.json"), summaryJson);

#if UNITY_EDITOR
        Debug.Log($"[WriteSessionJSON]\n{summaryJson}");
#endif

        // Post summary to parent (WebGL)
        WebGLBridge.PostJSON(summaryJson);
    }

    static void SafeWrite(string path, string content)
    {
        try { File.WriteAllText(path, content); }
        catch (System.Exception e) { Debug.LogWarning($"[LevelManager] Failed to write {path}\n{e}"); }
    }

    // =============== UI ===============
    void UpdateScoreText()
    {
        int displayTotal = _scoreCommitted + _scoreRun;
        if (_scoreTextTMP) _scoreTextTMP.text = $"{displayTotal}";
    }

    void UpdateHighScoreIfNeeded()
    {
        int current = _scoreCommitted + _scoreRun;
        if (current > _highScore)
        {
            _highScore = current;
            UpdateHighScoreText();
        }
    }

    void UpdateHighScoreText()
    {
        if (_highScoreTMP) _highScoreTMP.text = $"{_highScore}";
    }

    void UpdateLivesText()
    {
        if (_livesTMP == null) return;
        _livesTMP.text = _lives > 0 ? new string(livesGlyph, _lives) : string.Empty;
    }

    // Public getters
    public int CurrentScore => _scoreCommitted + _scoreRun;
    public int CurrentDeaths => _deaths;
    public int Lives => _lives;
    public int HighScore => _highScore;
}
