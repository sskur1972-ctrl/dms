(function () {
    'use strict';
    window.DmsEditorAdapters = window.DmsEditorAdapters || {};
    window.DmsEditorAdapters.COLLABORA = function (frame, form, host, settings) {
        var origin = new URL(settings.LaunchUrl).origin, page = null, loaded = false;
        var pendingCopy = null, pendingPage = null;
        function send(id, values) {
            frame.contentWindow.postMessage(JSON.stringify({ MessageId: id, SendTime: Date.now(), Values: values || {} }), origin);
        }
        function ready() { send('Host_PostmessageReady'); }
        function location(text) {
            var result = { page: page, text: text || '' };
            if (host.location) host.location(result);
            return result;
        }
        function receive(event) {
            if (event.origin !== origin || event.source !== frame.contentWindow) return;
            var message;
            try { message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data; } catch { return; }
            if (!message || typeof message !== 'object') return;
            var values = message.Values || {};
            if (message.MessageId === 'App_LoadingStatus') {
                ready();
                if (values.Status === 'Document_Loaded' && !loaded) { loaded = true; host.ready(); }
                if (values.Status === 'Failed') host.notice('Collabora could not load this document. Retry loading or check editor connectivity.');
            }
            if (message.MessageId === 'Doc_PartChanged') {
                var current = Number(values.Part);
                if (Number.isSafeInteger(current) && current > 0) { page = current; location(); }
            }
            if (message.MessageId === 'Action_Copy_Resp' && pendingCopy) {
                var content = typeof values.content === 'string' ? values.content : '';
                try { var parsed = JSON.parse(content); content = parsed['text/plain;charset=utf-8'] || parsed['text/plain'] || content; } catch { }
                pendingCopy.finish(location(content.slice(0, 4000)));
            }
            if (message.MessageId === 'Action_GoToPage_Resp' && pendingPage && Number(values.Page) === pendingPage.page)
                pendingPage.finish(values.success === true);
            if (message.MessageId === 'User_Active') host.activity();
            if (message.MessageId === 'Doc_ModifiedStatus') { var dirty = !!values.Modified; host.dirty(dirty); if (dirty) host.activity(); }
            if (message.MessageId === 'Error' || message.MessageId === 'Session_Closed') host.notice('The editor session closed or reported an error.');
        }
        function capture() {
            if (!loaded) return Promise.resolve({ page: page, text: '' });
            if (pendingCopy) return pendingCopy.promise;
            var request = {};
            request.promise = new Promise(function (resolve) {
                var timer = setTimeout(function () { request.finish({ page: page, text: '' }); }, 1500);
                request.finish = function (result) { clearTimeout(timer); pendingCopy = null; resolve(result); };
            });
            pendingCopy = request;
            // Reads the document selection without writing to the system clipboard.
            send('Action_Copy', { Mimetype: 'text/plain;charset=utf-8' });
            return request.promise;
        }
        async function navigate(anchor) {
            if (!loaded) return false;
            var moved = false, targetPage = Number(anchor.page);
            if (Number.isSafeInteger(targetPage) && targetPage > 0) {
                if (pendingPage) pendingPage.finish(false);
                moved = await new Promise(function (resolve) {
                    var request = { page: targetPage };
                    var timer = setTimeout(function () { request.finish(false); }, 2500);
                    request.finish = function (result) { clearTimeout(timer); pendingPage = null; resolve(result); };
                    pendingPage = request;
                    send('Action_GoToPage', { Page: targetPage });
                });
                if (!moved) return false;
            }
            var quote = anchor.text || anchor.section;
            if (quote) {
                send('Send_UNO_Command', { Command: '.uno:ExecuteSearch', Args: {
                    'SearchItem.SearchString': { type: 'string', value: quote },
                    'SearchItem.Backward': { type: 'boolean', value: false },
                    'SearchItem.Command': { type: 'long', value: 0 },
                    'SearchItem.SearchFlags': { type: 'long', value: 0 }
                } });
                moved = true;
            }
            if (moved) send('Grab_Focus');
            return moved;
        }
        return {
            initialize: function () { window.addEventListener('message', receive); frame.addEventListener('load', ready); form.submit(); },
            navigate: navigate, capture: capture,
            destroy: function () {
                if (pendingCopy) pendingCopy.finish({ page: page, text: '' });
                if (pendingPage) pendingPage.finish(false);
                window.removeEventListener('message', receive); frame.removeEventListener('load', ready); frame.src = 'about:blank';
            }
        };
    };
}());
