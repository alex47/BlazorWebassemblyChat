using SpacetimeDB;
using SpacetimeDB.Types;
using System.Collections.Concurrent;

namespace BlazorWebassembly.Services;

public class SpacetimeDbService
{
    private Identity? _Identity;
    private DbConnection? _DbConnection;
    private ConcurrentQueue<(string Command, string Args)> _InputQueue = new();

    public event Action<string>? OnNewMessage;

    public void InitConnection()
    {
        const string HOST = "http://localhost:3000";
        const string DB_NAME = "quickstart-chat";

        if (_DbConnection is not null && _DbConnection.IsActive)
        {
            Console.WriteLine("SpacetimeDB connection is already active.");
            return;
        }

        AuthToken.Init();

        _DbConnection = DbConnection.Builder()
            .WithUri(HOST)
            .WithModuleName(DB_NAME)
            .WithToken(AuthToken.Token)
            //.WithCompression(Compression.None)
            .OnConnect(OnConnected)
            .OnConnectError(OnConnectError)
            .OnDisconnect(OnDisconnected)
            .Build();

        Task.Run(async () => await ProcessTaskAsync(_DbConnection, CancellationToken.None));

        RegisterCallbacks(_DbConnection);
    }

    private void OnConnected(DbConnection conn, Identity identity, string authToken)
    {
        Console.WriteLine("OnConnected");

        _Identity = identity;

        // Can not save token to filesystem in webassembly
        //AuthToken.SaveToken(authToken);

        conn.SubscriptionBuilder()
            .OnApplied(OnSubscriptionApplied)
            .OnError(OnSubscriptionError)
            .SubscribeToAllTables();
    }

    private void OnConnectError(Exception e)
    {
        Console.WriteLine($"OnConnectError: {e}");
    }

    private void OnDisconnected(DbConnection conn, Exception? e)
    {
        if (e is null)
        {
            Console.WriteLine($"Disconnected normally.");
        }
        else
        {
            Console.WriteLine($"Disconnected abnormally: {e}");
        }
    }

    private void OnSubscriptionError(ErrorContext context, Exception exception)
    {
        Console.WriteLine($"OnSubscriptionError: {exception}");
    }

    private void OnSubscriptionApplied(SubscriptionEventContext ctx)
    {
        Console.WriteLine("OnSubscriptionApplied");
        PrintMessagesInOrder(ctx.Db);
    }

    private void RegisterCallbacks(DbConnection conn)
    {
        conn.Db.User.OnInsert += User_OnInsert;
        conn.Db.User.OnUpdate += User_OnUpdate;

        conn.Db.Message.OnInsert += Message_OnInsert;

        conn.Reducers.OnSetName += Reducer_OnSetNameEvent;
        conn.Reducers.OnSendMessage += Reducer_OnSendMessageEvent;
    }

    private void User_OnInsert(EventContext ctx, User insertedValue)
    {
        if (insertedValue.Online)
        {
            Console.WriteLine($"{UserNameOrIdentity(insertedValue)} is online");
        }
    }

    private void User_OnUpdate(EventContext ctx, User oldValue, User newValue)
    {
        if (oldValue.Name != newValue.Name)
        {
            Console.WriteLine($"{UserNameOrIdentity(oldValue)} renamed to {newValue.Name}");
        }
        if (oldValue.Online != newValue.Online)
        {
            if (newValue.Online)
            {
                Console.WriteLine($"{UserNameOrIdentity(newValue)} connected.");
            }
            else
            {
                Console.WriteLine($"{UserNameOrIdentity(newValue)} disconnected.");
            }
        }
    }

    private void Message_OnInsert(EventContext ctx, Message insertedValue)
    {
        if (ctx.Event is not Event<Reducer>.SubscribeApplied)
        {
            PrintMessage(ctx.Db, insertedValue);
        }
    }

    private void Reducer_OnSetNameEvent(ReducerEventContext ctx, string name)
    {
        var e = ctx.Event;

        if (e.CallerIdentity == _Identity && e.Status is Status.Failed(var error))
        {
            Console.Write($"Failed to change name to {name}: {error}");
        }
    }

    private void Reducer_OnSendMessageEvent(ReducerEventContext ctx, string text)
    {
        var e = ctx.Event;

        if (e.CallerIdentity == _Identity && e.Status is Status.Failed(var error))
        {
            Console.Write($"Failed to send message {text}: {error}");
        }
    }

    private async Task ProcessTaskAsync(DbConnection dbConnection, CancellationToken cancellationToken)
    {
        try
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                dbConnection.FrameTick();
                ProcessCommands(dbConnection.Reducers);
                await Task.Delay(100);
            }
        }
        finally
        {
            dbConnection.Disconnect();
        }
    }

    private void ProcessCommands(RemoteReducers reducers)
    {
        while (_InputQueue.TryDequeue(out var command))
        {
            switch (command.Command)
            {
                case "message":
                    reducers.SendMessage(command.Args);
                    break;
                case "name":
                    reducers.SetName(command.Args);
                    break;
            }
        }
    }

    private static string UserNameOrIdentity(User user) => user.Name ?? user.Identity.ToString()[..8];

    private void PrintMessage(RemoteTables tables, Message message)
    {
        var sender = tables.User.Identity.Find(message.Sender);
        var senderName = sender != null ? UserNameOrIdentity(sender) : "unknown";

        var msg = $"{senderName}: {message.Text}";
        OnNewMessage?.Invoke(msg); // _Instance defined below
    }

    private void PrintMessagesInOrder(RemoteTables tables)
    {
        foreach (Message message in tables.Message.Iter().OrderBy(item => item.Sent))
        {
            PrintMessage(tables, message);
        }
    }

    public void EnqueueMessage(string text)
    {
        if (text.StartsWith("/name "))
        {
            if (text.Length < 7)
            {
                Console.WriteLine("Invalid command: /name requires a name argument.");
                return;
            }

            text = text[6..]; // Remove "/name " prefix
            _InputQueue.Enqueue(("name", text));
        }
        else
        {
            _InputQueue.Enqueue(("message", text));
        }
    }
}
