using UnityEngine;
using System;

[RequireComponent(typeof(Collider))]
public class ObjectController : MonoBehaviour
{
    [Tooltip("Movement speed (units/sec). Applied each FixedUpdate.")]
    public Vector3 Speed = new Vector3(2f, 0f, 0f);

    [Tooltip("Optional: used for orientation/logic if you want it.")]
    public Transform TargetPlaceholder;

    [Tooltip("If a Rigidbody exists, movement uses MovePosition in FixedUpdate.")]
    public bool preferRigidbodyMove = true;

    public event Action<ObjectController> OnDespawn;

    Rigidbody _rb;
    Collider _col;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>();

        // We want trigger callbacks for the deadzone volume.
        // This object’s collider itself can be solid; deadzone volume should be isTrigger.
        if (_col == null)
        {
            // Guarantee we have at least a collider (BoxCollider recommended on prefab)
            _col = gameObject.AddComponent<BoxCollider>();
        }
    }

    void FixedUpdate()
    {
        Vector3 delta = Speed * Time.fixedDeltaTime;

        if (preferRigidbodyMove && _rb != null && !_rb.isKinematic)
        {
            _rb.MovePosition(_rb.position + delta);
        }
        else
        {
            transform.position += delta;
        }
    }

    // Deadzone handling: add a trigger volume in your scene (tagged "DeadZone") to clean up/recycle
    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Deadzone"))
        {
            Despawn();
        }
    }

    public void Despawn()
    {
        OnDespawn?.Invoke(this);
        Destroy(gameObject);
    }
}
