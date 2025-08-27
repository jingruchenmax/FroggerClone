# FroggerClone

This project is a simple clone of the classic Frogger game, built in Unity. It features basic gameplay mechanics where the player must navigate a frog across a busy road and river to reach safety.

## Research Context
This project is a clone created to replicate the Frogger-clone Cuber from the following research article:

> Hicks, Kieran; Gerling, Kathrin; Dickinson, Patrick; Vanden Abeele, Vero. "Juicy Game Design: Understanding the Impact of Visual Embellishments on Player Experience." CHI PLAY '19, 2019. [ACM Digital Library](https://doi.org/10.1145/3311350.3347171)

The goal is to explore the impact of visual embellishments (juiciness) on player experience, as described in the referenced study.

## How to Run
Open the project in Unity and run the main scene. Adjust difficulty and visual effects using the parameters in the URL if running a web build:

`index.html?difficultyLevel=3&isJuicy=0`

## WebGL Parent Communication

At the end of each level and at game completion, the game sends JSON messages to the WebGL parent using the following calls:

### At Level End
- `movement_complete`: Contains a JSON array of all movement events for the level.
- `collisons_complete`: Contains a JSON array of all collision events for the level.
- `summary_complete`: Contains a JSON array of summary data for the level.

### At Game End
- `game_complete`: Contains a JSON object summarizing the entire game session, including:
  - `totalPoints`: Total points scored.
  - `totalTime`: Total time spent.
  - `totalDeaths`: Total deaths.
  - `levelsCleared`: Number of levels completed.
  - `difficultyLevel`: The difficulty multiplier used.
  - `isJuicy`: Whether juicy visual effects were enabled.

These messages are sent using the `WebGLBridge.PostJSON` method for integration with the parent web page.