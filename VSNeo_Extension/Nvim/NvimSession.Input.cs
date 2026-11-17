using System;
using System.Threading;

namespace VSNeo_Extension.Nvim;

// Sending keys, and the hold that replays them once a pending switch lands.
internal sealed partial class NvimSession
{
    /// <summary>Forward keys. Fire and forget by design: the decision was already made locally.</summary>
    public void Input(string keys)
    {
        var client = _client;
        if (client == null || !IsReady)
        {
            return;
        }

        if (Volatile.Read(ref _holdGeneration) != 0)
        {
            if (TryHold(keys))
            {
                return;
            }
            // Past the cap or the deadline: the hold is over. The keys held
            // so far go first, in order, then this one - sending this one
            // alone would put it ahead of everything typed before it.
            FlushHold(client);
        }

        Infrastructure.Perf.KeySent();
        client.NotifyInput(keys);
    }

    /// <summary>Ends the hold early and sends what it held, in order.</summary>
    private void FlushHold(NvimRpcClient client)
    {
        string[] held;
        lock (_held)
        {
            Volatile.Write(ref _holdGeneration, 0);
            held = _held.ToArray();
            _held.Clear();
        }

        if (held.Length > 0)
        {
            Infrastructure.Log.Write("input hold overflowed; sending " + held.Length + " held keys now");
        }

        foreach (var k in held)
        {
            client.NotifyInput(k);
        }
    }

    // A document switch in flight. From the moment a view takes focus until
    // nvim_win_set_buf lands, nvim's window still shows the *previous*
    // document, and a quick dd or x typed into the new one used to edit that
    // background tab through its mirror. Keys are held here meanwhile and
    // replayed, in order, once the switch (and the caret push after it) is
    // done. Bounded: past the cap or the deadline the keys go straight
    // through again, which is what always happened before.
    private int _holdGeneration;

    private int _holdStartedTicks;

    private readonly System.Collections.Generic.List<string> _held = new System.Collections.Generic.List<string>();

    private const int MaxHeldKeys = 32;

    private const int MaxHoldMs = 500;

    /// <summary>Starts holding keys. Returns the generation to end it with.</summary>
    public int BeginInputHold()
    {
        lock (_held)
        {
            int generation = Interlocked.Increment(ref _holdGenerationSeq);
            Volatile.Write(ref _holdStartedTicks, Environment.TickCount);
            Volatile.Write(ref _holdGeneration, generation);
            return generation;
        }
    }

    private int _holdGenerationSeq;

    /// <summary>
    /// Ends the hold started with <paramref name="generation"/>. A newer hold
    /// (focus moved on again) supersedes it: the keys stay held and replay
    /// when that one ends, into the document the user is now looking at.
    /// </summary>
    public void EndInputHold(int generation, bool replay)
    {
        string[] held;
        lock (_held)
        {
            if (Volatile.Read(ref _holdGeneration) != generation)
            {
                return;
            }

            Volatile.Write(ref _holdGeneration, 0);
            held = _held.ToArray();
            _held.Clear();
        }

        if (held.Length == 0)
        {
            return;
        }

        if (!replay)
        {
            Infrastructure.Log.Write("dropping " + held.Length + " held keys: the document switch failed");
            return;
        }

        var client = _client;
        if (client == null || !IsReady)
        {
            return;
        }

        foreach (var keys in held)
        {
            client.NotifyInput(keys);
        }
    }

    private bool TryHold(string keys)
    {
        lock (_held)
        {
            if (Volatile.Read(ref _holdGeneration) == 0)
            {
                return false;
            }

            if (_held.Count >= MaxHeldKeys
                || unchecked(Environment.TickCount - Volatile.Read(ref _holdStartedTicks)) > MaxHoldMs)
            {
                return false;
            }

            _held.Add(keys);
            return true;
        }
    }
}
