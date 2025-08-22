using UnityEngine;
using UnityEngine.Events;

public class CompletionPointController : MonoBehaviour
{
    public enum FinishState { Idle, Done }

    [Header("State")]
    [SerializeField] private FinishState currentState = FinishState.Idle;

    [Tooltip("If true, once set to Done, further calls are ignored.")]
    public bool playOnce = true;

    [Header("Juicy")]
    [Tooltip("If false, visual flair (like particles) is suppressed.")]
    public bool isJuicy = true;

    [Tooltip("Optional particle system to play when entering Done (only if isJuicy is true). If null, will try to find one in children.")]
    public ParticleSystem completionParticles;

    [Header("Events")]
    [Tooltip("Called exactly once when transitioning into Done.")]
    public UnityEvent OnDone;

    [Tooltip("Called whenever the state changes (Idle or Done).")]
    public UnityEvent<FinishState> OnStateChanged;

    /// <summary>External callers (like PlayerController) can do SetState("Done")</summary>
    public void SetState(string state)
    {
        if (string.Equals(state, "Done", System.StringComparison.OrdinalIgnoreCase))
        {
            SetDone();
        }
        else if (string.Equals(state, "Idle", System.StringComparison.OrdinalIgnoreCase))
        {
            ResetPoint();
        }
        else
        {
            Debug.LogWarning($"[CompletionPointController] Unknown state '{state}'. Use 'Idle' or 'Done'.", this);
        }
    }

    /// <summary>Set the finish point to Done and fire events (and optional FX if juicy).</summary>
    public void SetDone()
    {
        if (playOnce && currentState == FinishState.Done) return;

        bool wasDone = currentState == FinishState.Done;
        currentState = FinishState.Done;

        // Notify listeners about state change
        OnStateChanged?.Invoke(currentState);

        // Only act on the transition into Done
        if (!wasDone)
        {
            // Play particles if allowed by isJuicy and reference exists
            if (isJuicy && completionParticles != null)
            {
                completionParticles.Play(true);
            }

            // Invoke user-assigned actions
            OnDone?.Invoke();
        }
    }

    /// <summary>Reset back to Idle (for level restart / testing).</summary>
    public void ResetPoint()
    {
        if (currentState == FinishState.Idle)
        {
            OnStateChanged?.Invoke(currentState);
            return;
        }

        currentState = FinishState.Idle;
        OnStateChanged?.Invoke(currentState);
    }

    /// <summary>Query if this finish is already completed.</summary>
    public bool IsDone => currentState == FinishState.Done;

#if UNITY_EDITOR
    void OnValidate()
    {
        // Ensure events are non-null for fresh components in the editor
        if (OnDone == null) OnDone = new UnityEvent();
        if (OnStateChanged == null) OnStateChanged = new UnityEvent<FinishState>();

        // Auto-assign a child particle system if not set
        if (completionParticles == null)
            completionParticles = GetComponentInChildren<ParticleSystem>(true);
    }

    void OnDrawGizmosSelected()
    {
        var col = IsDone ? Color.green : Color.yellow;
        Gizmos.color = col;
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.25f, 0.25f);
    }
#endif
}
