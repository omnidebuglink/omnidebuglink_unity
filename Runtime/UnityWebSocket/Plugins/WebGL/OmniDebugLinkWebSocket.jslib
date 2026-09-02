var OdlWebSocketLibrary =
{
    $odlWebSocketManager:
    {
        /*
         * Map of instances
         *
         * Instance structure:
         * {
         *     url: string,
         *     ws: WebSocket,
         *	   binaryType: string,
         *     subProtocols: string[],
         * }
         */
        instances: {},

        /* Last instance ID */
        lastId: 0,

        /* Event listeners */
        onOpen: null,
        onMessage: null,
        onError: null,
        onClose: null
    },

    /**
     * Set onOpen callback
     *
     * @param callback Reference to C# static function
     */
    OdlWebSocketSetOnOpen: function(callback)
    {
        odlWebSocketManager.onOpen = callback;
    },

    /**
     * Set onMessage callback
     *
     * @param callback Reference to C# static function
     */
    OdlWebSocketSetOnMessage: function(callback)
    {
        odlWebSocketManager.onMessage = callback;
    },

    /**
     * Set onMessage callback
     *
     * @param callback Reference to C# static function
     */
    OdlWebSocketSetOnMessageStr: function(callback)
    {
        odlWebSocketManager.onMessageStr = callback;
    },

    /**
     * Set onError callback
     *
     * @param callback Reference to C# static function
     */
    OdlWebSocketSetOnError: function(callback)
    {
        odlWebSocketManager.onError = callback;
    },

    /**
     * Set onClose callback
     *
     * @param callback Reference to C# static function
     */
    OdlWebSocketSetOnClose: function(callback)
    {
        odlWebSocketManager.onClose = callback;
    },

    /**
     * Allocate new WebSocket instance struct
     *
     * @param url Server URL
     */
    OdlWebSocketAllocate: function(urlPtr, binaryTypePtr)
    {
        var url = UTF8ToString(urlPtr);
        var binaryType = UTF8ToString(binaryTypePtr);
        var id = ++odlWebSocketManager.lastId;
        odlWebSocketManager.instances[id] = {
            url: url,
            ws: null,
            binaryType: binaryType
        };

        return id;
    },

    /**
     * Add Sub Protocol
     *
     * @param instanceId Instance ID
     * @param protocol Sub Protocol
     */
    OdlWebSocketAddSubProtocol: function(instanceId, protocolPtr)
    {
        var instance = odlWebSocketManager.instances[instanceId];
        if (!instance) return -1;

        var protocol = UTF8ToString(protocolPtr);
        
        if(instance.subProtocols == null) 
            instance.subProtocols = []; 

        instance.subProtocols.push(protocol);

        return 0;
    },

    /**
     * Remove reference to WebSocket instance
     *
     * If socket is not closed function will close it but onClose event will not be emitted because
     * this function should be invoked by C# WebSocket destructor.
     *
     * @param instanceId Instance ID
     */
    OdlWebSocketFree: function(instanceId)
    {
        var instance = odlWebSocketManager.instances[instanceId];
        if (!instance) return 0;

        // Close if not closed
        if (instance.ws !== null && instance.ws.readyState < 2)
            instance.ws.close();

        // Remove reference
        delete odlWebSocketManager.instances[instanceId];

        return 0;
    },
    
    /**
     * Connect WebSocket to the server
     *
     * @param instanceId Instance ID
     */
    OdlWebSocketConnect: function(instanceId)
    {
        var instance = odlWebSocketManager.instances[instanceId];
        if (!instance) return -1;
        if (instance.ws !== null) return -2;

        if(instance.subProtocols != null)
            instance.ws = new WebSocket(instance.url, instance.subProtocols);
        else
            instance.ws = new WebSocket(instance.url);

        instance.ws.binaryType = instance.binaryType;

        instance.ws.onopen = function()
        {
            Module.dynCall_vi(odlWebSocketManager.onOpen, instanceId);
        };

        instance.ws.onmessage = function(ev)
        {
            if (ev.data instanceof ArrayBuffer)
            {
                var array = new Uint8Array(ev.data);
                var buffer = _malloc(array.length);
                writeArrayToMemory(array, buffer);
                try
                {
                    Module.dynCall_viii(odlWebSocketManager.onMessage, instanceId, buffer, array.length);
                }
                finally
                {
                    _free(buffer);
                }
            }
            else if(typeof ev.data == 'string')
            {
                var length = lengthBytesUTF8(ev.data) + 1;
                var buffer = _malloc(length);
                stringToUTF8(ev.data, buffer, length);
                try
                {
                    Module.dynCall_vii(odlWebSocketManager.onMessageStr, instanceId, buffer);
                }
                finally
                {
                    _free(buffer);
                }
            }
            else if (ev.data instanceof Blob)
            {
                var reader = new FileReader();
                reader.onload = function()
                {
                    var array = new Uint8Array(reader.result);
                    var buffer = _malloc(array.length);
                    writeArrayToMemory(array, buffer);
                    try
                    {
                        Module.dynCall_viii(odlWebSocketManager.onMessage, instanceId, buffer, array.length);
                    }
                    finally
                    {
                        reader = null;
                        _free(buffer);
                    }
                };
                reader.readAsArrayBuffer(ev.data);
            }
            else
            {
                console.log("[JSLIB WebSocket] not support message type: ", (typeof ev.data));
            }
        };

        instance.ws.onerror = function(ev)
        {
            var msg = "WebSocket error.";
            var length = lengthBytesUTF8(msg) + 1;
            var buffer = _malloc(length);
            stringToUTF8(msg, buffer, length);
            try
            {
                Module.dynCall_vii(odlWebSocketManager.onError, instanceId, buffer);
            }
            finally
            {
                _free(buffer);
            }
        };

        instance.ws.onclose = function(ev)
        {
            var msg = ev.reason;
            var length = lengthBytesUTF8(msg) + 1;
            var buffer = _malloc(length);
            stringToUTF8(msg, buffer, length);
            try
            {
                Module.dynCall_viii(odlWebSocketManager.onClose, instanceId, ev.code, buffer);
            }
            finally
            {
                _free(buffer);
            }
            instance.ws = null;
        };

        return 0;
    },

    /**
     * Close WebSocket connection
     *
     * @param instanceId Instance ID
     * @param code Close status code
     * @param reasonPtr Pointer to reason string
     */
    OdlWebSocketClose: function(instanceId, code, reasonPtr)
    {
        var instance = odlWebSocketManager.instances[instanceId];
        if (!instance) return -1;
        if (instance.ws === null) return -3;
        if (instance.ws.readyState === 2) return -4;
        if (instance.ws.readyState === 3) return -5;

        var reason = ( reasonPtr ? UTF8ToString(reasonPtr) : undefined );
        try
        {
            instance.ws.close(code, reason);
        }
        catch(err)
        {
            return -7;
        }

        return 0;
    },

    /**
     * Send message over WebSocket
     *
     * @param instanceId Instance ID
     * @param bufferPtr Pointer to the message buffer
     * @param length Length of the message in the buffer
     */
    OdlWebSocketSend: function(instanceId, bufferPtr, length)
    {
        var instance = odlWebSocketManager.instances[instanceId];
        if (!instance) return -1;
        if (instance.ws === null) return -3;
        if (instance.ws.readyState !== 1) return -6;

        instance.ws.send(buffer.slice(bufferPtr, bufferPtr + length));

        return 0;
    },

    /**
     * Send message string over WebSocket
     *
     * @param instanceId Instance ID
     * @param stringPtr Pointer to the message string
     */
    OdlWebSocketSendStr: function(instanceId, stringPtr)
    {
        var instance = odlWebSocketManager.instances[instanceId];
        if (!instance) return -1;
        if (instance.ws === null) return -3;
        if (instance.ws.readyState !== 1) return -6;

        instance.ws.send(UTF8ToString(stringPtr));

        return 0;
    },

    /**
     * Return WebSocket readyState
     *
     * @param instanceId Instance ID
     */
    OdlWebSocketGetState: function(instanceId)
    {
        var instance = odlWebSocketManager.instances[instanceId];
        if (!instance) return -1;
        if (instance.ws === null) return 3; // socket null as closed
        
        return instance.ws.readyState;
    }
};

autoAddDeps(OdlWebSocketLibrary, '$odlWebSocketManager');
mergeInto(LibraryManager.library, OdlWebSocketLibrary);
