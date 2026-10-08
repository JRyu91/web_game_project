mergeInto(LibraryManager.library, {
  WebConfig: function() {
    var text = JSON.stringify(window.gameSession);
    var ptr = _malloc(lengthBytesUTF8(text) + 1);
    stringToUTF8(text, ptr, lengthBytesUTF8(text) + 1);
    return ptr;
  },
  WebConnect: function(url, join, generation) {
    var notify = function(text) {
      SendMessage('NetworkClient', 'WebEvent', JSON.stringify({generation: generation, text: text}));
    };
    try {
      var socket = new WebSocket(UTF8ToString(url));
      var hello = UTF8ToString(join);
      window.gameSocket = socket;
      socket.onopen = function() { notify('{"type":"transport_open"}'); socket.send(hello); };
      socket.onmessage = function(e) { if (typeof e.data === 'string') notify(e.data); };
      socket.onclose = function() { notify('{"type":"closed"}'); };
      socket.onerror = function() { socket.close(); };
    } catch (e) { notify('{"type":"closed"}'); }
  },
  WebSend: function(text) {
    var socket = window.gameSocket;
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(UTF8ToString(text));
  },
  WebChatToggle: function() { if (window.gameChat) window.gameChat.toggle(); },
  WebChatLine: function(line) { if (window.gameChat) window.gameChat.line(UTF8ToString(line)); },
  WebClose: function() {
    if (window.gameSocket) { window.gameSocket.close(); window.gameSocket = null; }
  }
});
