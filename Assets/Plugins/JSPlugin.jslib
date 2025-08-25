mergeInto(LibraryManager.library, {
  SendGameEventMessage: function(typePtr, jsonPtr) {
    var type = UTF8ToString(typePtr);   // event type, e.g., "game_event" or "player_action"
    var json = UTF8ToString(jsonPtr);   // payload as JSON string
    window.parent.postMessage({
      type: type,
      payload: JSON.parse(json)
    }, "*");
  }
});
