using System;
using UnityEngine;

[Serializable]
public struct MovementEvent
{
    public string level;
    public int resetIndex;
    public double timeSinceLevelStart; // seconds
    public Vector3 from;
    public Vector3 to;
    public Vector3 direction;          // world dir of the step
    public bool juicy;                 // rolling vs instant jump
    public float stepSize;             // units
}

[Serializable]
public struct CollisionEvent
{
    public string level;
    public int resetIndex;
    public double timeSinceLevelStart; // seconds
    public string otherName;
    public string otherTag;
    public string phase;               // "Enter" / "Exit"
}

[Serializable]
public struct LevelSummary
{
    public string level;
    public int resetIndex;
    public double durationSeconds;
    public int deaths;
    public int score;                  // 10 per forward step, +100 on finish
}

// JSON container helpers (JsonUtility needs a root)
[Serializable] public class MovementEventList { public MovementEvent[] items; }
[Serializable] public class CollisionEventList { public CollisionEvent[] items; }
