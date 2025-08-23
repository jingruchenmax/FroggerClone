using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class PlayerController : MonoBehaviour
{
    // Grace period timer for water after leaving wood/land (non-juicy mode)
    private float _waterGraceTimer = 0f;
    private const float WaterGracePeriod = 0.05f;
    [Header("Mode")]
    public bool juicy = true; // true = rolling; false = instant grid jump

    [Header("Common")]
    public Transform spawnPoint;
    public string obstacleTag = "Obstacle";

    [Header("Step Settings")]
    [Min(0.1f)] public float stepSize = 1f;          // distance per input (both modes)
    [Min(0.05f)] public float rollDuration = 0.15f;  // time to complete a 90� roll (juicy)

    [Header("Tags")]
    public string woodTag = "wood";
    public string landTag = "land";
    public string finishTag = "FinishPoint";

    // --- Juicy "hit off" FX (only used when juicy == true) ---
    [Header("Juicy Hit-Off (Death FX)")]
    [Tooltip("Upward force added on juicy death.")]
    public float hitUpForce = 7.5f;
    [Tooltip("Horizontal knockback force added on juicy death.")]
    public float hitBackForce = 10f;
    [Tooltip("Random torque magnitude applied on juicy death.")]
    public float hitTorque = 20f;
    [Tooltip("Seconds to wait before respawning after juicy death.")]
    public float respawnDelay = 2f;
    [Tooltip("Multiplier for force if the obstacle is moving fast into the player.")]
    public float relativeSpeedBoost = 0.5f;
    [Tooltip("Optional: cap the total knockback force magnitude.")]
    public float maxTotalHitForce = 20f;

    [Header("Respawn")]
    [Tooltip("Short sleep so the player fully settles on respawn before physics resumes.")]
    public float postRespawnSleep = 0.2f;

    Rigidbody _rb;
    Vector3 _spawnPos;
    Quaternion _spawnRot;
    bool _isRolling;

    // --- Parallel carry (no parenting) ---
    bool _onCarrier;
    Transform _carrierTf;
    Vector3 _carrierLastPos;
    Vector3 _carrierSpeed; // preferred: from ObjectController.Speed

    // Remember original constraints so we can restore on leaving land / respawn
    RigidbodyConstraints _originalConstraints;

    // Internal guard to avoid multiple death triggers while flying off
    bool _isJuicyHitOffActive;

    // NEW: coroutine handles (fixes StopAllCoroutines self-cancel issue)
    Coroutine _rollRoutine;
    Coroutine _hitOffRoutine;

    // ========= EVENTS FOR LEVELMANAGER =========
    public static System.Action<MovementEvent> OnPlayerMoved;      // one per step
    public static System.Action<CollisionEvent> OnPlayerCollision; // enter/exit
    public static System.Action OnPlayerDeath;                     // before respawn
    public static System.Action OnPlayerReachedFinish;             // when touching FinishPoint
    // ===========================================

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        if (spawnPoint != null)
        {
            _spawnPos = spawnPoint.position;
            _spawnRot = spawnPoint.rotation;
        }
        else
        {
            _spawnPos = transform.position;
            _spawnRot = transform.rotation;
        }

        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        _originalConstraints = _rb.constraints;
    }

    private void Start()
    {
        if (!juicy)
        {
            // Set Rigidbody to kinematic and disable gravity
            _rb.isKinematic = true;
            _rb.useGravity = false;
            // Set Collider to trigger
            var col = GetComponent<Collider>();
            if (col != null)
                col.isTrigger = true;

            // Disable Animator safely
            var anim = GetComponent<Animator>();
            if (anim != null)
                anim.enabled = false;

            // Disable child ParticleSystem GameObject safely
            var ps = GetComponentInChildren<ParticleSystem>(true);
            if (ps != null && ps.gameObject != null)
                ps.gameObject.SetActive(false);
        }
    }

    void Update()
    {
        if (juicy)
        {
            if (_isRolling || _isJuicyHitOffActive) return; // block input while flying off
            Vector3 dir = ReadCardinalKeyDown();
            if (dir != Vector3.zero && _rollRoutine == null)
                _rollRoutine = StartCoroutine(RollStep(dir)); // start tracked roll
        }
        else
        {
            // INSTANT JUMP: one cell per key press, no lerp
            Vector3 dir = ReadCardinalKeyDown();
            if (dir != Vector3.zero)
            {
                Vector3 from = transform.position;
                Vector3 to = from + dir * stepSize;
                transform.position = to;
                EmitMove(from, to, dir); // <<< fire movement event
            }

            // Water grace period logic
            if (_waterGraceTimer > 0f)
            {
                _waterGraceTimer -= Time.deltaTime;
                if (_waterGraceTimer <= 0f)
                {
                    // After grace period, check if still only touching water
                    Collider[] hits = Physics.OverlapSphere(transform.position, 0.25f);
                    bool stillTouchingWater = false, touchingLandOrWood = false;
                    foreach (var col in hits)
                    {
                        if (col.CompareTag("Water")) stillTouchingWater = true;
                        if (col.CompareTag(landTag) || col.CompareTag(woodTag)) touchingLandOrWood = true;
                    }
                    if (stillTouchingWater && !touchingLandOrWood)
                    {
                        OnPlayerDeath?.Invoke();
                        Respawn();
                    }
                }
            }
        }
    }

    void FixedUpdate()
    {
        if (_onCarrier && _carrierTf != null && !_isJuicyHitOffActive)
        {
            Vector3 worldDelta = _carrierTf.position - _carrierLastPos;
            transform.position += worldDelta;
            _carrierLastPos = _carrierTf.position;
        }
    }


    #region Juicy Roll
    IEnumerator RollStep(Vector3 dir)
    {
        _isRolling = true;
        bool originalKinematic = _rb.isKinematic;
        _rb.isKinematic = true;

        float half = stepSize * 0.5f;
        Vector3 pivot = transform.position + (dir * half) + (Vector3.down * half);
        Vector3 axis = Vector3.Cross(Vector3.up, dir);

        Quaternion startRot = transform.rotation;
        Vector3 startPos = transform.position;

        float elapsed = 0f;
        const float totalAngle = 90f;

        while (elapsed < rollDuration)
        {
            float t = Mathf.Clamp01(elapsed / rollDuration);
            float angle = Mathf.Lerp(0f, totalAngle, t);
            Quaternion deltaRot = Quaternion.AngleAxis(angle, axis);

            Vector3 rotatedPos = RotatePointAroundPivot(startPos, pivot, deltaRot);
            transform.SetPositionAndRotation(rotatedPos, deltaRot * startRot);

            elapsed += Time.deltaTime;
            yield return null;
        }

        Quaternion endDelta = Quaternion.AngleAxis(totalAngle, axis);
        Vector3 endPos = RotatePointAroundPivot(startPos, pivot, endDelta);
        transform.SetPositionAndRotation(endPos, endDelta * startRot);

        _rb.isKinematic = originalKinematic;
        _isRolling = false;

        // Emit movement now that the roll has completed
        EmitMove(startPos, endPos, dir);

        // clear tracked roll
        _rollRoutine = null;
    }

    static Vector3 RotatePointAroundPivot(Vector3 point, Vector3 pivot, Quaternion delta)
    {
        return pivot + delta * (point - pivot);
    }
    #endregion

    #region Input
    Vector3 ReadCardinalKeyDown()
    {
        if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow)) return Vector3.forward;
        else if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow)) return Vector3.back;
        else if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) return Vector3.left;
        else if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) return Vector3.right;
        else return Vector3.zero;
    }
    #endregion

    #region Death/Respawn & Collisions
    void OnCollisionEnter(Collision collision)
    {
        if (collision.collider.CompareTag(obstacleTag))
        {
            // Fire death event immediately (existing behavior)
            OnPlayerDeath?.Invoke();

            if (juicy)
            {
                // Juicy: fling off screen, then respawn after a delay
                if (!_isJuicyHitOffActive && _hitOffRoutine == null)
                    _hitOffRoutine = StartCoroutine(JuicyHitOffAndRespawn(collision.collider.transform, collision.relativeVelocity.magnitude));
            }
            else
            {
                Respawn(); // non-juicy: instant
            }
            return;
        }

        HandleSurfaceOnEnter(collision.collider);
        HandleFinishOnEnter(collision.collider);

        EmitCollision(collision.collider, "Enter");
    }

    void OnTriggerEnter(Collider other)
    {
        if (!juicy)
        {
            if (other.CompareTag(obstacleTag))
            {
                OnPlayerDeath?.Invoke();
                Respawn();
                EmitCollision(other, "Enter");
                return;
            }
            HandleNonJuicyWaterLogic();
            EmitCollision(other, "Enter");
            return;
        }

        if (other.CompareTag(obstacleTag))
        {
            // Fire death event immediately (existing behavior)
            OnPlayerDeath?.Invoke();

            if (juicy)
            {
                if (!_isJuicyHitOffActive && _hitOffRoutine == null)
                    _hitOffRoutine = StartCoroutine(JuicyHitOffAndRespawn(other.transform, 0f));
            }
            return;
        }

        HandleSurfaceOnEnter(other);
        HandleFinishOnEnter(other);

        EmitCollision(other, "Enter");
    // Handles non-juicy mode water/land/wood/finish logic
    void HandleNonJuicyWaterLogic()
    {
        // Get all overlapping colliders at the player's position
        Collider[] hits = Physics.OverlapSphere(transform.position, 0.25f);
        bool hasWater = false, hasLandOrWood = false, hasFinish = false;
        Collider waterCol = null;
        foreach (var hit in hits)
        {
            if (hit.CompareTag("Water")) { hasWater = true; waterCol = hit; }
            if (hit.CompareTag(landTag) || hit.CompareTag(woodTag)) hasLandOrWood = true;
            if (hit.CompareTag(finishTag)) hasFinish = true;
        }

        if (hasWater && hasLandOrWood)
        {
            // Continue with land/wood logic
            foreach (var hit in hits)
            {
                if (hit.CompareTag(landTag) || hit.CompareTag(woodTag))
                {
                    HandleSurfaceOnEnter(hit);
                    break;
                }
            }
            return;
        }
        if (hasWater && hasFinish)
        {
            // Continue with finish logic only
            foreach (var hit in hits)
            {
                if (hit.CompareTag(finishTag))
                {
                    HandleFinishOnEnter(hit);
                    break;
                }
            }
            return;
        }
        if (hasWater && !hasLandOrWood && !hasFinish)
        {
            // Water only: treat as obstacle and reset
            OnPlayerDeath?.Invoke();
            Respawn();
            return;
        }
        // Otherwise, default logic
        foreach (var hit in hits)
        {
            if (hit.CompareTag(landTag) || hit.CompareTag(woodTag)) HandleSurfaceOnEnter(hit);
            if (hit.CompareTag(finishTag)) HandleFinishOnEnter(hit);
        }
    }
    }

    void OnCollisionExit(Collision collision)
    {
        HandleSurfaceOnExit(collision.collider);
        EmitCollision(collision.collider, "Exit");
    }

    void OnTriggerExit(Collider other)
    {
        HandleSurfaceOnExit(other);
        EmitCollision(other, "Exit");
    }

    void Respawn()
    {
        ClearCarrierFollow();

        // Hard stop any motion first
        // Only set velocity/angVel if not kinematic
        if (!_rb.isKinematic)
        {
            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        // Restore transform and constraints
        transform.SetPositionAndRotation(_spawnPos, _spawnRot);
        _rb.constraints = _originalConstraints;

        // Stop/clear known routines (targeted) and flags
        if (_rollRoutine != null) { StopCoroutine(_rollRoutine); _rollRoutine = null; }
        if (_hitOffRoutine != null) { StopCoroutine(_hitOffRoutine); _hitOffRoutine = null; }
        _isRolling = false;
        _isJuicyHitOffActive = false;

        // --- NEW: Surface Re-check ---
        Collider[] hits = Physics.OverlapSphere(transform.position, 0.25f);
        foreach (Collider hit in hits)
        {
            Debug.Log("Respawn check hit: " + hit.name + " tag: " + hit.tag);
            if (hit.CompareTag(woodTag) || hit.CompareTag(landTag))
            {
                HandleSurfaceOnEnter(hit);
                break;
            }
        }
    }


    #endregion

    #region Surfaces (wood/land) without parenting + land freeze
    void HandleSurfaceOnEnter(Collider hit)
    {
        if (hit.CompareTag(woodTag) || hit.CompareTag(landTag))
        {
            // Begin following motion (no parenting)
            _onCarrier = true;
            _carrierTf = hit.attachedRigidbody != null ? hit.attachedRigidbody.transform : hit.transform;
            _carrierLastPos = _carrierTf.position;

            // Prefer reading mover speed from ObjectController (world units/sec)
            _carrierSpeed = Vector3.zero;
            var oc = hit.GetComponentInParent<ObjectController>();
            if (oc != null) _carrierSpeed = oc.Speed;
            _rb.constraints = RigidbodyConstraints.FreezeRotation;
            return;
        }
    }

    void HandleSurfaceOnExit(Collider hit)
    {
        if ((hit.CompareTag(woodTag) || hit.CompareTag(landTag)) && _carrierTf == hit.transform)
        {
            ClearCarrierFollow();
            // Leaving land: release rotation freeze
            _rb.constraints = _originalConstraints;

            // If non-juicy, check if still touching water
            if (!juicy)
            {
                Collider[] hits = Physics.OverlapSphere(transform.position, 0.25f);
                bool stillTouchingWater = false, touchingLandOrWood = false;
                foreach (var col in hits)
                {
                    if (col.CompareTag("Water")) stillTouchingWater = true;
                    if (col.CompareTag(landTag) || col.CompareTag(woodTag)) touchingLandOrWood = true;
                }
                if (stillTouchingWater && !touchingLandOrWood)
                {
                    // Start grace period timer
                    _waterGraceTimer = WaterGracePeriod;
                }
            }
        }
    // Reset water grace timer
    _waterGraceTimer = 0f;
    }


    void ClearCarrierFollow()
    {
        _onCarrier = false;
        _carrierTf = null;
        _carrierSpeed = Vector3.zero;
    }
    #endregion

    #region Finish Points
    void HandleFinishOnEnter(Collider hit)
    {
        if (!hit.CompareTag(finishTag)) return;

        var cpc = hit.GetComponentInParent<CompletionPointController>();
        if (cpc != null) cpc.SetState("Done");

        OnPlayerReachedFinish?.Invoke();
    }
    #endregion

    #region Emit helpers
    void EmitMove(Vector3 from, Vector3 to, Vector3 dir)
    {
        var ev = new MovementEvent
        {
            level = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            resetIndex = LevelManager.Instance != null ? LevelManager.Instance.ResetsCompleted : 0,
            timeSinceLevelStart = LevelManager.Instance != null ? LevelManager.Instance.ElapsedSeconds : Time.timeSinceLevelLoadAsDouble,
            from = from,
            to = to,
            direction = dir,
            juicy = juicy,
            stepSize = stepSize
        };
        OnPlayerMoved?.Invoke(ev);
    }

    void EmitCollision(Collider other, string phase)
    {
        var ev = new CollisionEvent
        {
            level = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            resetIndex = LevelManager.Instance != null ? LevelManager.Instance.ResetsCompleted : 0,
            timeSinceLevelStart = LevelManager.Instance != null ? LevelManager.Instance.ElapsedSeconds : Time.timeSinceLevelLoadAsDouble,
            otherName = other.name,
            otherTag = other.tag,
            phase = phase
        };
        OnPlayerCollision?.Invoke(ev);
    }
    #endregion

    #region Juicy Hit-Off Helpers
    IEnumerator JuicyHitOffAndRespawn(Transform obstacle, float relativeSpeed)
    {
        _isJuicyHitOffActive = true;

        // Stop only the roll coroutine (do NOT StopAllCoroutines here)
        if (_rollRoutine != null)
        {
            StopCoroutine(_rollRoutine);
            _rollRoutine = null;
        }
        _isRolling = false;
        ClearCarrierFollow();

        // Ensure physics is active and free to tumble
        _rb.isKinematic = false;
        _rb.constraints = RigidbodyConstraints.None;

        // Zero out current motion before applying the blast
        _rb.velocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;

        // Compute a fun knockback direction: away from obstacle, plus a pop upwards
        Vector3 away = (transform.position - obstacle.position);
        away.y = 0f;
        if (away.sqrMagnitude < 0.001f) away = Random.insideUnitSphere; // degenerate overlap
        away.Normalize();

        Vector3 knockDir = (away + Vector3.up).normalized;

        // Scale by configured strengths; give extra oomph if obstacle was fast
        float speedBonus = Mathf.Max(0f, relativeSpeed) * relativeSpeedBoost;
        float totalForceMag = Mathf.Min(hitBackForce + hitUpForce + speedBonus, maxTotalHitForce);

        Vector3 force = knockDir * totalForceMag;
        _rb.AddForce(force, ForceMode.Impulse);

        // Add some chaotic spin
        Vector3 randomTorque = new Vector3(
            Random.Range(-1f, 1f),
            Random.Range(-1f, 1f),
            Random.Range(-1f, 1f)
        ).normalized * hitTorque;
        _rb.AddTorque(randomTorque, ForceMode.Impulse);

        // Optional: trigger Animator / particles if present (non-destructive)
        var anim = GetComponent<Animator>();
        if (anim != null)
        {
            // Check if the trigger parameter exists before setting it
            bool hasHitOff = false;
            foreach (var param in anim.parameters)
            {
                if (param.type == AnimatorControllerParameterType.Trigger && param.name == "HitOff")
                {
                    hasHitOff = true;
                    break;
                }
            }
            if (hasHitOff)
            {
                anim.SetTrigger("HitOff");
            }
        }
        var ps = GetComponentInChildren<ParticleSystem>(true);
        if (ps != null) ps.Play(true);

        // Wait, then respawn cleanly
        yield return new WaitForSeconds(respawnDelay);

        // Clear our own handle BEFORE calling Respawn in case Respawn stops coroutines
        _hitOffRoutine = null;

        Respawn();
    }

    #endregion
}
