using System.Collections;
using UnityEngine;

// [RequireComponent(typeof(Collider))] // Collider still needed for triggers
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

    // Rigidbody removed
    Vector3 _spawnPos;
    Quaternion _spawnRot;
    bool _isRolling;
    private bool _canMove = true;
    // --- Parallel carry (no parenting) ---
    bool _onCarrier;
    Transform _carrierTf;
    Vector3 _carrierLastPos;
    Vector3 _carrierSpeed; // preferred: from ObjectController.Speed

    // Remember original constraints so we can restore on leaving land / respawn
    Rigidbody _rb;
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

    Vector3 dir;
    // Axis debouncing state
    private float prevHorizontal = 0f;
    private float prevVertical = 0f;
    // Grid-based MovePosition state
    private Vector3 targetPosition;
    private bool isMoving = false;

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
        targetPosition = transform.position;
        _originalConstraints = _rb.constraints;
    }

    private void Start()
    {
        SetJuicyState();
        Respawn();
    }

    public void SetJuicyState()
    {
        if (!juicy)
        {
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
        // --- Debounced axis input ---
        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical = Input.GetAxisRaw("Vertical");
        dir = Vector3.zero;

        if (juicy)
        {
            // Only allow movement in one cardinal direction at a time
            if (Mathf.Abs(horizontal) > Mathf.Abs(vertical))
            {
                // Debounce horizontal
                if (prevHorizontal == 0 && Mathf.Abs(horizontal) > 0.5f)
                    dir = horizontal > 0 ? Vector3.right : Vector3.left;
            }
            else if (Mathf.Abs(vertical) > 0.1f)
            {
                // Debounce vertical
                if (prevVertical == 0 && Mathf.Abs(vertical) > 0.5f)
                    dir = vertical > 0 ? Vector3.forward : Vector3.back;
            }

            // Juicy mode: only roll once per press, block if already rolling or hit-off is active
            if (juicy)
            {
                if (!_isRolling && !_isJuicyHitOffActive && dir != Vector3.zero && _rollRoutine == null && _canMove)
                {
                    _canMove = false;
                    _rollRoutine = StartCoroutine(RollStep(dir));
                }
                else if (Mathf.Abs(horizontal) < 0.1f && Mathf.Abs(vertical) < 0.1f)
                {
                    _canMove = true; // Reset on axis release
                }
            }
        }
        else
        {
            // Only allow movement in one cardinal direction at a time
            if (!isMoving)
            {
                if (Mathf.Abs(horizontal) > Mathf.Abs(vertical))
                {
                    // Debounce horizontal
                    if (prevHorizontal == 0 && Mathf.Abs(horizontal) > 0.5f)
                        dir = horizontal > 0 ? Vector3.right : Vector3.left;
                }
                else if (Mathf.Abs(vertical) > 0.1f)
                {
                    // Debounce vertical
                    if (prevVertical == 0 && Mathf.Abs(vertical) > 0.5f)
                        dir = vertical > 0 ? Vector3.forward : Vector3.back;
                }
                if (dir != Vector3.zero)
                {
                    targetPosition = transform.position + dir * stepSize;
                    isMoving = true;
                }
            }
            // Water grace period logic
            if (_waterGraceTimer > 0f)
            {
                _waterGraceTimer -= Time.deltaTime;
                if (_waterGraceTimer <= 0f)
                {
                    // After grace period, check if still only touching water
                    Collider[] hits = Physics.OverlapSphere(transform.position, 0.1f);
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

        // Update previous axis values for debouncing
        prevHorizontal = Mathf.Abs(horizontal) > 0.1f ? horizontal : 0f;
        prevVertical = Mathf.Abs(vertical) > 0.1f ? vertical : 0f;
    }



    void FixedUpdate() {
        // No physics-based movement
        if (!juicy && isMoving) {
            Vector3 from = transform.position;
            Vector3 to = targetPosition;
            Vector3 moveDir = (to - from).normalized;
            transform.position = to; // snap to grid
            isMoving = false;
            EmitMove(from, to, moveDir);
        }
        if (_onCarrier && _carrierTf != null && !_isJuicyHitOffActive) {
            Vector3 worldDelta = _carrierTf.position - _carrierLastPos;
            transform.position += worldDelta;
            _carrierLastPos = _carrierTf.position;
        }
    }


    #region Juicy Roll
    IEnumerator RollStep(Vector3 dir)
    {
        _isRolling = true;

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

        // Snap final rotation to nearest 90-degree increment to avoid floating-point drift
        Quaternion endDelta = Quaternion.AngleAxis(totalAngle, axis);
        Vector3 endPos = RotatePointAroundPivot(startPos, pivot, endDelta);
        Quaternion snappedRot = SnapRotationTo90(endDelta * startRot);
        transform.SetPositionAndRotation(endPos, snappedRot);

        _isRolling = false;

        // Emit movement now that the roll has completed
        EmitMove(startPos, endPos, dir);

        // clear tracked roll
        _rollRoutine = null;
    }

    // Utility: Snap quaternion rotation to nearest 90-degree increment on each axis
    Quaternion SnapRotationTo90(Quaternion rot)
    {
        Vector3 euler = rot.eulerAngles;
        euler.x = Mathf.Round(euler.x / 90f) * 90f;
        euler.y = Mathf.Round(euler.y / 90f) * 90f;
        euler.z = Mathf.Round(euler.z / 90f) * 90f;
        return Quaternion.Euler(euler);
    }

    static Vector3 RotatePointAroundPivot(Vector3 point, Vector3 pivot, Quaternion delta)
    {
        return pivot + delta * (point - pivot);
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

        // Restore transform and physics state
        transform.SetPositionAndRotation(_spawnPos, _spawnRot);
        var col = GetComponent<Collider>();
        if (_rb != null)
        {
            _rb.isKinematic = true; // Always kinematic except during hit-off
            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.constraints = _originalConstraints;
        }
        // Re-enable collider and ensure it's not a trigger
        if (col != null) {
            col.enabled = true;
            col.isTrigger = true;
        }

        // Stop/clear known routines (targeted) and flags
        if (_rollRoutine != null) { StopCoroutine(_rollRoutine); _rollRoutine = null; }
        if (_hitOffRoutine != null) { StopCoroutine(_hitOffRoutine); _hitOffRoutine = null; }
        _isRolling = false;
        _isJuicyHitOffActive = false;
        _canMove = true;

        // --- NEW: Surface Re-check ---
        Collider[] hits = Physics.OverlapSphere(transform.position, 0.25f);
        foreach (Collider hit in hits)
        {
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
            _carrierTf = hit.transform;
            _carrierLastPos = _carrierTf.position;

            // Prefer reading mover speed from ObjectController (world units/sec)
            _carrierSpeed = Vector3.zero;
            var oc = hit.GetComponentInParent<ObjectController>();
            if (oc != null) _carrierSpeed = oc.Speed;
            return;
        }
    }

    void HandleSurfaceOnExit(Collider hit)
    {
        if ((hit.CompareTag(woodTag) || hit.CompareTag(landTag)) && _carrierTf == hit.transform)
        {
            ClearCarrierFollow();

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

        // Dramatic physics-based knockback
        _rb.isKinematic = false; // Only non-kinematic during hit-off
        _rb.constraints = RigidbodyConstraints.None;
        _rb.velocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;

        // Temporarily disable collider to avoid extra deaths
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;

        Vector3 away = (transform.position - obstacle.position);
        away.y = 0f;
        if (away.sqrMagnitude < 0.001f) away = Random.insideUnitSphere;
        away.Normalize();
        Vector3 knockDir = (away + Vector3.up * 1.5f).normalized;
        float speedBonus = Mathf.Max(0f, relativeSpeed) * relativeSpeedBoost;
        float totalForceMag = Mathf.Min(hitBackForce * 2f + hitUpForce * 2f + speedBonus * 2f, maxTotalHitForce * 2f);
        Vector3 force = knockDir * totalForceMag;
        _rb.AddForce(force, ForceMode.Impulse);

        // Add dramatic spin
        Vector3 randomTorque = new Vector3(
            Random.Range(-2f, 2f),
            Random.Range(-2f, 2f),
            Random.Range(-2f, 2f)
        ).normalized * hitTorque * 2f;
        _rb.AddTorque(randomTorque, ForceMode.Impulse);

        // Optional: trigger Animator / particles if present (non-destructive)
        var anim = GetComponent<Animator>();
        if (anim != null)
        {
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
