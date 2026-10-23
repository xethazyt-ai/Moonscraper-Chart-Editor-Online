// See LICENSE in project root for license information.

// Checks that cursors, ghost notes and the song's details (name, artist and so on) reach everyone in a session.

using System;
using System.Collections.Generic;

public static class PresenceTests
{
    static int s_failures = 0;

    public static int Run()
    {
        SessionTests.TestSong hostSong = new SessionTests.TestSong("presence-host");
        SessionTests.TestSong aliceSong = new SessionTests.TestSong("presence-alice");
        SessionTests.TestSong bobSong = new SessionTests.TestSong("presence-bob");
        hostSong.song.name = "Through the Fire and Flames";
        hostSong.song.metaData.artist = "DragonForce";
        hostSong.song.iniProperties = SongIniFunctions.SongIniFromString("delay = 0\nloading_phrase = Hold on tight");

        DuetSession host = DuetSession.Host(hostSong, "Robert", 0);
        DuetSession alice = DuetSession.Join(aliceSong, "Alex", "127.0.0.1", host.port);
        List<DuetSession> all = new List<DuetSession> { host, alice };

        // The host is already pointing somewhere before Alex joins
        DuetPresence robert = new DuetPresence { color = 4, viewTick = 768, instrument = "Guitar", difficulty = "Expert", hasPointer = true, pointerX = -1.5f, pointerTick = 960 };
        robert.ghosts.Add(new DuetPresence.Ghost { tick = 960, rawNote = 2, flags = 4 });
        host.SetLocalPresence(robert);

        SessionTests.Pump(all, () => alice.state == DuetSession.State.Connected && alice.presences.ContainsKey(0));
        DuetPresence seen;
        alice.presences.TryGetValue(0, out seen);
        Expect("a joiner sees the host's cursor straight away", seen != null && seen.hasPointer && seen.pointerTick == 960 && seen.pointerX == -1.5f && seen.color == 4);
        Expect("a joiner sees the host's ghost note", seen != null && seen.ghosts.Count == 1 && seen.ghosts[0].tick == 960 && seen.ghosts[0].rawNote == 2 && seen.ghosts[0].flags == 4);
        Expect("a joiner gets the song's details, custom ini settings included",
            aliceSong.song.name == "Through the Fire and Flames" && aliceSong.song.metaData.artist == "DragonForce"
            && SongIniFunctions.IniTextFromSongProperties(aliceSong.song.iniProperties).Contains("Hold on tight"));
        Expect("a joiner knows their own user id", alice.localUserId == 1);

        // Alex moves; the host and a second joiner see it
        DuetSession bob = DuetSession.Join(bobSong, "Sam", "127.0.0.1", host.port);
        all.Add(bob);
        SessionTests.Pump(all, () => bob.state == DuetSession.State.Connected && bob.users.Count == 3 && alice.users.Count == 3);

        DuetPresence alex = new DuetPresence { color = 0, viewTick = 1536, instrument = "Drums", difficulty = "Hard" };
        alice.SetLocalPresence(alex);
        SessionTests.Pump(all, () => host.presences.ContainsKey(1) && bob.presences.ContainsKey(1));
        Expect("the host sees a joiner's presence", host.presences.ContainsKey(1) && host.presences[1].viewTick == 1536 && !host.presences[1].hasPointer);
        Expect("other joiners see it too, through the host", bob.presences.ContainsKey(1) && bob.presences[1].instrument == "Drums");
        Expect("nobody gets their own presence back", !alice.presences.ContainsKey(1));

        // Chat: a joiner says something, then the host answers
        alice.SendChat("  can you take the solo?\n");
        SessionTests.Pump(all, () => host.chat.Count == 1 && bob.chat.Count == 1);
        Expect("a chat message reaches everyone, with who sent it", host.chat.Count == 1 && bob.chat.Count == 1 && alice.chat.Count == 1
            && bob.chat[0].name == "Alex" && bob.chat[0].userId == 1 && bob.chat[0].text == "can you take the solo?");
        host.SendChat("sure");
        alice.SendChat("   ");
        SessionTests.Pump(all, () => alice.chat.Count == 2 && bob.chat.Count == 2);
        Expect("the host's messages reach everyone, and blank ones are dropped", alice.chat.Count == 2 && bob.chat.Count == 2 && host.chat.Count == 2
            && alice.chat[1].name == "Robert" && alice.chat[1].text == "sure");

        DuetPresence typing = new DuetPresence { color = 0, viewTick = 1536, instrument = "Drums", difficulty = "Hard", isTyping = true };
        alice.SetLocalPresence(typing);
        SessionTests.Pump(all, () => bob.presences.ContainsKey(1) && bob.presences[1].isTyping);
        Expect("everyone sees who's typing", bob.presences.ContainsKey(1) && bob.presences[1].isTyping && host.presences[1].isTyping);

        // Song details change on a joiner, and on the host at about the same time, in different fields
        aliceSong.song.metaData.charter = "Alex";
        hostSong.song.metaData.album = "Inhuman Rampage";
        SessionTests.Pump(all, () => hostSong.song.metaData.charter == "Alex" && bobSong.song.metaData.charter == "Alex"
            && aliceSong.song.metaData.album == "Inhuman Rampage" && bobSong.song.metaData.album == "Inhuman Rampage");
        Expect("a joiner's change to the song's details reaches everyone", hostSong.song.metaData.charter == "Alex" && bobSong.song.metaData.charter == "Alex");
        Expect("a change on the host at the same time isn't lost", aliceSong.song.metaData.album == "Inhuman Rampage" && bobSong.song.metaData.album == "Inhuman Rampage"
            && hostSong.song.metaData.charter == "Alex");

        bobSong.song.offset = 0.25f;
        SessionTests.Pump(all, () => hostSong.song.offset == 0.25f && aliceSong.song.offset == 0.25f);
        Expect("the offset syncs exactly", hostSong.song.offset == 0.25f && aliceSong.song.offset == 0.25f);

        // When Alex leaves, their cursor goes away for everyone
        alice.Leave();
        SessionTests.Pump(all, () => !host.presences.ContainsKey(1) && !bob.presences.ContainsKey(1));
        Expect("a leaver's cursor disappears", !host.presences.ContainsKey(1) && !bob.presences.ContainsKey(1));
        Expect("a joiner that got the chart says so", alice.hasReceivedSong && bob.hasReceivedSong);

        SessionTests.TestSong carolSong = new SessionTests.TestSong("presence-carol");
        DuetSession carol = DuetSession.Join(carolSong, "Casey", "127.0.0.1", host.port);
        all.Add(carol);
        SessionTests.Pump(all, () => carol.state == DuetSession.State.Connected && carol.chat.Count == 2);
        Expect("someone joining later sees what was said", carol.chat.Count == 2 && carol.chat[0].name == "Alex" && carol.chat[1].text == "sure");

        // A locked session turns new people away but keeps everyone already in
        host.isLocked = true;
        DuetSession dave = DuetSession.Join(new SessionTests.TestSong("presence-dave"), "Dev", "127.0.0.1", host.port);
        all.Add(dave);
        SessionTests.Pump(all, () => !dave.isActive);
        Expect("nobody new can join a locked session", !dave.isActive && dave.endReason.Contains("locked") && carol.isActive && host.users.Count == 3);
        host.isLocked = false;

        // A full session turns new people away too
        host.maxUsers = 3;
        DuetSession erin = DuetSession.Join(new SessionTests.TestSong("presence-erin"), "Erin", "127.0.0.1", host.port);
        all.Add(erin);
        SessionTests.Pump(all, () => !erin.isActive);
        Expect("nobody new can join a full session", !erin.isActive && erin.endReason.Contains("full") && host.users.Count == 3);
        host.maxUsers = 1;
        Expect("the room size stays between 2 and 64", host.maxUsers == 2);
        host.maxUsers = 100;
        Expect("the room size can't go past 64", host.maxUsers == 64);

        host.Leave();
        SessionTests.Pump(all, () => !bob.isActive && !carol.isActive);

        int pink = DuetPresence.MakeCustomColor(new UnityEngine.Color(1f, 0.5f, 0.25f));
        UnityEngine.Color back = DuetPresence.GetColor(pink);
        Expect("a custom color comes back the same", DuetPresence.IsCustomColor(pink) && back.r == 1f && Math.Abs(back.g - 128 / 255f) < 0.001f && Math.Abs(back.b - 64 / 255f) < 0.001f);
        Expect("a custom color is named by its hex code", DuetPresence.GetColorName(pink) == "#FF8040" && DuetPresence.GetColorName(2) == "Yellow");
        Expect("presets and custom colors are valid, nonsense isn't", DuetPresence.IsValidColor(pink) && DuetPresence.IsValidColor(7) && !DuetPresence.IsValidColor(8) && !DuetPresence.IsValidColor(-1));
        DuetPresence customRound = DuetPresence.FromJson((Dictionary<string, object>)DuetJson.Read(DuetJson.Write(new DuetPresence { color = pink }.ToJson())));
        Expect("a custom color survives being sent", customRound.color == pink);

        DuetPresence round = DuetPresence.FromJson((Dictionary<string, object>)DuetJson.Read(DuetJson.Write(robert.ToJson())));
        Expect("presence survives being written and read", DuetJson.Write(round.ToJson()) == DuetJson.Write(robert.ToJson()));

        return s_failures;
    }

    static void Expect(string label, bool condition)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + label);
        if (!condition)
            ++s_failures;
    }
}
