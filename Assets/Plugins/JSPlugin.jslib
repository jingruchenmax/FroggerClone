mergeInto(LibraryManager.library, {
  SendGameEventMessage: function(jsonPtr) {
    var json = UTF8ToString(jsonPtr);
    window.parent.postMessage({
      type: "game_event",
      payload: JSON.parse(json)
    }, "*");
  }
});