using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Windows.Forms;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// One thing the host has to be told about the Windows startup entry: which sentence, and
    /// the path that sentence names when it names one.
    /// <para>
    /// It is an ID and never a sentence. The words live in the catalog, so a host reading the
    /// window in Japanese reads the note in Japanese, and a sentence written in C# would
    /// arrive on that screen in English however the rest of the window was drawn.
    /// </para>
    /// </summary>
    public sealed class StartupNotice
    {
        /// <summary>The catalog id of the sentence the Upkeep card draws under the switch.</summary>
        public string Id { get; init; }

        /// <summary>
        /// The path the sentence names, for the two notes that name one, or null for the notes
        /// that do not. It rides beside the id rather than inside it for the same reason the id
        /// is an id: the sentence around it belongs to the catalog.
        /// </summary>
        public string Path { get; init; }
    }

    /// <summary>
    /// Everything the card has to say under the switch right now, which is one sentence or two.
    /// <para>
    /// WHY TWO. The machine wide Run key and this account's own Run key are separate facts, and
    /// a run can be looking at a bad one of each at the same time: Windows starts a different
    /// copy for every account on this PC, AND Windows refused to write or to remove this
    /// account's own entry. Until 1.2.5 the machine sentence simply won, so the refusal was
    /// said once by the save that met it and was then gone on the next open of the card, which
    /// is the very disappearing note this whole road exists to end. Both are drawn now, the
    /// machine one first because it is the bigger fact about the PC, the account one under it
    /// because it is the half the switch is lying about.
    /// </para>
    /// </summary>
    public sealed class StartupNotes
    {
        /// <summary>The sentence drawn directly under the switch, or null when there is none.</summary>
        public StartupNotice Notice { get; init; }

        /// <summary>
        /// The second sentence, drawn under the first. Never filled without
        /// <see cref="Notice"/>, because a second note with nothing above it is the first note.
        /// </summary>
        public StartupNotice Also { get; init; }

        /// <summary>Nothing to say, which is what a healthy machine answers.</summary>
        public static readonly StartupNotes None = new();
    }

    /// <summary>
    /// What a save of the "start with Windows" preference did, and anything the host has to
    /// be told about it.
    /// </summary>
    public sealed class StartupOutcome
    {
        /// <summary>True when the registry was actually changed.</summary>
        public bool Changed { get; init; }

        /// <summary>
        /// What the card draws under the switch now that the road has run. It is worked out by
        /// <see cref="StartupEntry.NotesFor"/>, from a fresh read, which is the SAME method the
        /// card asks on every open: that is what keeps the sentence a save shows and the
        /// sentence the next open shows one sentence rather than two that disagree.
        /// </summary>
        public StartupNotes Notes { get; init; } = StartupNotes.None;

        /// <summary>The first note, or null. A shorthand for <see cref="Notes"/>.</summary>
        public StartupNotice Notice => Notes?.Notice;

        /// <summary>The second note, or null. A shorthand for <see cref="Notes"/>.</summary>
        public StartupNotice AlsoNotice => Notes?.Also;

        /// <summary>The id of the first note, or null.</summary>
        public string NoticeId => Notice?.Id;

        /// <summary>The path the first note names, or null.</summary>
        public string NoticePath => Notice?.Path;

        /// <summary>The id of the second note, or null.</summary>
        public string AlsoNoticeId => AlsoNotice?.Id;

        /// <summary>The path the second note names, or null.</summary>
        public string AlsoNoticePath => AlsoNotice?.Path;
    }

    /// <summary>
    /// The "start with Windows" preference, and the one Run key entry that carries it.
    /// <para>
    /// WHY THIS SHAPE. Until 1.2.5 every operation, reads included, opened the Run key for
    /// WRITING and tried the machine wide hive first. An ordinary account is refused on that
    /// hive every time, so every read of the preference threw, was caught, and wrote a line
    /// into the log that said nothing had gone wrong. Worse, an entry written during a run as
    /// administrator lives in the machine hive, a normal run cannot open that hive for
    /// writing, so the read never saw the entry and turning the switch OFF left BakaLoader
    /// starting with Windows.
    /// </para>
    /// <para>
    /// So: reads are read only and look in both hives, because a read of either hive is
    /// allowed and never throws. Writes only ever name the current user's hive, because the
    /// preference is this account's and needs no elevation. An entry in the machine hive is
    /// one this application will not create, will try to remove when the switch goes off and
    /// that entry names THIS executable, and will say plainly what it is when it names
    /// another one or when Windows refuses to remove it.
    /// </para>
    /// <para>
    /// The rule under all of it: the switch on the card must never read one thing while
    /// Windows does another. Every road that cannot finish what the switch says answers a
    /// note instead of going quiet, on the save AND on every read of the card after it: a
    /// note only the save can produce is a note that is gone as soon as the window closes.
    /// That is not a promise kept by hand. Every road ends in <see cref="Answer"/>, which asks
    /// <see cref="NotesFor"/>, which is the one and only place a note is decided and the same
    /// one the card asks on every open.
    /// </para>
    /// </summary>
    public sealed class StartupEntry
    {
        /// <summary>
        /// The note the card draws when an entry in the machine hive names THIS executable and
        /// outlived the switch, because an ordinary run cannot take it away.
        /// </summary>
        public const string MachineHiveNoticeId = "hearth.upkeep.start_windows.note.machine";

        /// <summary>
        /// The note the card draws when an entry in the machine hive names some OTHER copy of
        /// BakaLoader. It starts that copy for every account on this PC, this application will
        /// not write that hive, and an ordinary run cannot remove the entry either, so the note
        /// names the path and the two ways out.
        /// </summary>
        public const string MachineOtherPathNoticeId =
            "hearth.upkeep.start_windows.note.machine_other";

        /// <summary>
        /// The note the card draws when this account's entry names another copy of BakaLoader
        /// that is STILL THERE. Which of two installed copies Windows should start is the
        /// host's to say, so the app names the path and leaves the entry alone.
        /// </summary>
        public const string OtherCopyNoticeId = "hearth.upkeep.start_windows.note.other_copy";

        /// <summary>
        /// The note the card draws when this PC already starts THIS copy for every account and
        /// this account's own entry goes on naming another one. Both roads that meet that state
        /// ask Windows to take the stale entry away, so an entry still standing over it is one
        /// Windows would not let go of, and the remedy
        /// <see cref="OtherCopyNoticeId"/> gives, turning the switch off and on again, walks
        /// straight back into the same refusal. This sentence names the way out that works
        /// instead, and it needs no administrator rights.
        /// </summary>
        public const string OtherCopyStuckNoticeId =
            "hearth.upkeep.start_windows.note.other_copy_stuck";

        /// <summary>
        /// The note the card draws when Windows refused the write for this account. The switch
        /// is saved and reads on, and nothing would start, so the card says so rather than
        /// letting the switch stand for something that did not happen.
        /// </summary>
        public const string RefusedWriteNoticeId = "hearth.upkeep.start_windows.note.refused";

        /// <summary>
        /// The note the card draws when the switch reads OFF and this account's entry is still
        /// standing. Windows refusing the delete is the road that usually leads there, and
        /// until 1.2.5 that road said nothing at all: no note, no line in the log, and a switch
        /// standing for something that never happened.
        /// <para>
        /// The sentence names the state and the two ways out rather than the cause, because the
        /// state is what a read can see: a preferences file that was reset, or carried from
        /// another machine, leaves the same entry behind without any refusal in it. It names no
        /// path either, so it is the honest answer whichever copy of BakaLoader that leftover
        /// entry happens to point at.
        /// </para>
        /// </summary>
        public const string RefusedDeleteNoticeId =
            "hearth.upkeep.start_windows.note.refused_off";

        private readonly IRunKeyRegistry Registry;

        /// <summary>
        /// Whether a path on disk is there. A seam, because the re-point at launch turns on
        /// whether the copy the entry names is still installed, and a test cannot be left
        /// depending on what happens to be on this machine's drives.
        /// </summary>
        private readonly Func<string, bool> FileThere;

        /// <summary>
        /// One warning per run, per kind. The refusal is the same refusal every time the host
        /// moves the switch, and a line repeated on every save is a line that gets skipped.
        /// </summary>
        private bool WarnedAboutMachineHive;

        /// <inheritdoc cref="WarnedAboutMachineHive"/>
        private bool WarnedAboutRefusedWrite;

        /// <inheritdoc cref="WarnedAboutMachineHive"/>
        private bool WarnedAboutRefusedDelete;

        /// <summary>
        /// Whether a write for this account was refused during this run and has not landed
        /// since.
        /// <para>
        /// It is remembered because a refused write leaves NOTHING behind to read: no entry in
        /// either hive, and a preference that still says on. <see cref="NotesFor"/> works from
        /// reads alone, so without this it could only answer null, and the note would live in
        /// the one answer that save handed back. The card is drawn again on every open, and the
        /// launch road throws its answer away, so the host would restart into a switch reading
        /// on, nothing starting with Windows, and not a word on the card.
        /// </para>
        /// </summary>
        private bool WriteWasRefused;

        public StartupEntry(IRunKeyRegistry registry, Func<string, bool> fileExists = null)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            FileThere = fileExists ?? System.IO.File.Exists;
        }

        /// <summary>
        /// Puts the Run key where the preference says, for this account, and answers whether
        /// anything moved and what the card has to say about it.
        /// </summary>
        /// <param name="userPreference">The saved preference, after the save landed.</param>
        /// <param name="entryName">The value's name under the Run key.</param>
        /// <param name="exePath">The executable the entry should point at.</param>
        /// <param name="logger">Where the one line per real change goes.</param>
        public StartupOutcome Apply(bool userPreference, string entryName, string exePath, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(entryName)) return new StartupOutcome();

            // Both reads are read only, so neither of them can throw and neither of them has
            // anything to log.
            var userEntry = Registry.Read(RunKeyHive.CurrentUser, entryName);
            var machineEntry = Registry.Read(RunKeyHive.LocalMachine, entryName);

            return userPreference
                ? TurnOn(entryName, exePath, userEntry, machineEntry, logger)
                : TurnOff(entryName, exePath, userEntry, machineEntry, logger);
        }

        /// <summary>
        /// Everything the card should be drawing right now, worked out from reads alone. It is
        /// asked on every read of the preferences, and again at the end of every road that
        /// changed something, so a host who reopens the window reads the same sentence the save
        /// showed them rather than a different one.
        /// <para>
        /// TWO NOTES, NOT ONE. The machine hive comes first: an entry there starts a copy of
        /// BakaLoader for every account on the PC, which is the bigger fact and the one that
        /// takes a run as administrator to put right. This account's own state is answered
        /// underneath it rather than instead of it, because the two can be wrong at once and
        /// the account one is the half the switch stands for.
        /// </para>
        /// <para>
        /// With the switch OFF, an entry still standing under this account is the refused
        /// delete: the save asked Windows to take it away and Windows said no, so the switch
        /// reads off while Windows goes on starting BakaLoader. It is answered whichever copy
        /// that entry names, because the delete is not path qualified either.
        /// </para>
        /// <para>
        /// With the switch ON and no entry anywhere, there is nothing on disk to read, so the
        /// refused WRITE is the one note that cannot be worked out from the registry. It is
        /// remembered for the run instead, in <see cref="WriteWasRefused"/>, and answered here
        /// from that. A run where no write was refused says nothing, because an account with no
        /// entry yet is not the same thing as an account Windows refused.
        /// </para>
        /// <para>
        /// With the switch ON, a machine entry naming THIS copy and an account entry naming
        /// another one, the account entry is one both roads asked Windows to remove, so an
        /// entry still standing there is a refused delete. That one IS on disk to read, so it
        /// is read rather than remembered, and it gets its own sentence because the remedy the
        /// ordinary two copies note gives, turning the switch off and on again, is the very
        /// thing Windows just refused.
        /// </para>
        /// <para>
        /// Nothing here writes anything. Both reads are read only, which a normal account is
        /// allowed to do in either hive.
        /// </para>
        /// </summary>
        public StartupNotes NotesFor(bool userPreference, string entryName, string exePath)
        {
            if (string.IsNullOrWhiteSpace(entryName)) return StartupNotes.None;

            var machineEntry = Registry.Read(RunKeyHive.LocalMachine, entryName);
            var userEntry = Registry.Read(RunKeyHive.CurrentUser, entryName);

            var machine = MachineNote(userPreference, machineEntry, exePath);
            var account = AccountNote(userPreference, userEntry, machineEntry, exePath);

            // A second note with nothing above it is simply the first note.
            return machine == null
                ? new StartupNotes { Notice = account }
                : new StartupNotes { Notice = machine, Also = account };
        }

        /// <summary>
        /// The first of <see cref="NotesFor"/>, for a caller that draws one sentence. The card
        /// draws both.
        /// </summary>
        public StartupNotice NoticeFor(bool userPreference, string entryName, string exePath) =>
            NotesFor(userPreference, entryName, exePath).Notice;

        /// <summary>
        /// What the machine wide hive has to say, which is a fact about this PC rather than
        /// about this account: nothing an ordinary run can do reaches it.
        /// </summary>
        private static StartupNotice MachineNote(bool userPreference, string machineEntry, string exePath)
        {
            if (machineEntry == null) return null;

            // Naming another copy is worth saying whichever way the switch reads: with it off,
            // Windows is still starting something; with it on, Windows is starting the WRONG
            // something, for every account, and this run can change neither.
            if (!Same(machineEntry, exePath))
                return new StartupNotice { Id = MachineOtherPathNoticeId, Path = machineEntry };

            // It names this copy, so with the switch on it is doing exactly what the switch
            // says. With the switch off it is the entry that outlived it.
            return userPreference ? null : new StartupNotice { Id = MachineHiveNoticeId };
        }

        /// <summary>
        /// And what THIS ACCOUNT'S own Run key has to say, which is the half the switch on the
        /// card actually stands for.
        /// </summary>
        private StartupNotice AccountNote(
            bool userPreference, string userEntry, string machineEntry, string exePath)
        {
            if (!userPreference)
                return userEntry == null
                    ? null
                    : new StartupNotice { Id = RefusedDeleteNoticeId };

            if (userEntry == null)
                return WriteWasRefused ? new StartupNotice { Id = RefusedWriteNoticeId } : null;

            if (Same(userEntry, exePath)) return null;

            // With the PC already starting THIS copy for every account, this account's entry
            // needed no write and no re-point: it needed to GO, and both roads that reach this
            // state ask Windows to take it away. An entry still standing over that is one
            // Windows would not let go of, and it is the one case where the other_copy remedy
            // is worse than useless: turning the switch off and on again walks the same refused
            // delete twice and leaves the host exactly where they started. The way out that
            // works is outside this window, and it needs no administrator rights.
            if (Same(machineEntry, exePath))
                return new StartupNotice { Id = OtherCopyStuckNoticeId, Path = userEntry };

            // An entry naming a copy that is no longer on disk is the moved install, and the
            // launch puts that one right on its own. One that is still on disk is two copies
            // and a question only the host can answer.
            if (FileThere(userEntry))
                return new StartupNotice { Id = OtherCopyNoticeId, Path = userEntry };

            // Unless the write that would have put it right is the one Windows refused. The
            // refusal is the same refusal whether this account had no entry at all or a
            // leftover one naming a folder that is gone: the registry keeps a value pointing
            // at a path that is not there, nothing starts with Windows, and the switch reads
            // on. Both roads reach here. The save writes because the entry names something
            // else, the launch writes because the copy it names has gone from disk, and each
            // of them is refused into exactly this shape.
            return WriteWasRefused ? new StartupNotice { Id = RefusedWriteNoticeId } : null;
        }

        /// <summary>
        /// The one thing a launch puts right on its own: the switch is on and this account's
        /// entry does not name the executable that is running. Answers whether it moved
        /// anything, and what the card has to say when it did not.
        /// <para>
        /// WHY IT IS HERE AND NOT ON THE SAVE. Until 1.2.4 the bridge applied this preference
        /// whenever the key arrived, and the Upkeep card posts all of its switches together, so
        /// any save of that card walked <see cref="TurnOn"/> and re-pointed a stale entry on the
        /// way past. An ordinary save must write nothing to the registry, so that road is closed
        /// now, and the self-heal cannot live on it. It lives at launch instead, which is the
        /// first thing that happens after a host moves or renames the folder.
        /// </para>
        /// <para>
        /// THE RULE IT FOLLOWS. With the switch on, this account's entry is written when there
        /// is none at all, and re-pointed when it names a copy of BakaLoader that is no longer
        /// on disk. It is LEFT ALONE when the copy it names is still installed: opening an old
        /// copy once must not quietly take startup away from the one the host has moved to, so
        /// the card names the copy Windows will start and the host says which one they meant.
        /// An entry in the machine hive is left exactly where it is either way, because this
        /// application does not write that hive.
        /// </para>
        /// <para>
        /// A machine entry that names ANOTHER copy no longer ends this road early. It did until
        /// 1.2.5, and that cost two things: a write Windows would have refused was never even
        /// attempted, so the refusal was never remembered and the card had nothing to draw, and
        /// a host who cleared that machine row partway through a session was left with the
        /// switch reading on, nothing in either hive, and a blank card. This account's entry is
        /// this account's to get right whatever the other hive holds.
        /// </para>
        /// <para>
        /// A machine entry that names THIS copy still ends the write, because the PC already
        /// starts it for every account and a second entry would start it twice. It no longer
        /// ends the road: a stale account entry naming ANOTHER copy is cleared here the way the
        /// save clears it, so a host who moved folders and never touches the switch again is
        /// not left with two copies starting.
        /// </para>
        /// </summary>
        public StartupOutcome RepointIfMoved(
            bool userPreference, string entryName, string exePath, ILogger logger)
        {
            if (!userPreference
                || string.IsNullOrWhiteSpace(entryName)
                || string.IsNullOrWhiteSpace(exePath)) return new StartupOutcome();

            var machineEntry = Registry.Read(RunKeyHive.LocalMachine, entryName);
            var userEntry = Registry.Read(RunKeyHive.CurrentUser, entryName);

            // An entry for every account on this PC that already names THIS executable does the
            // job, and a second one under this account would only mean Windows started
            // BakaLoader twice. What this account's entry must not do is go on naming ANOTHER
            // copy, which is the same rule the save road follows, and the launch is where a
            // host who never touches the switch again meets it.
            if (Same(machineEntry, exePath))
                return Answer(
                    ClearStaleAccountEntry(entryName, exePath, userEntry, logger),
                    true, entryName, exePath);

            if (Same(userEntry, exePath)) return Answer(false, true, entryName, exePath);

            if (userEntry != null && FileThere(userEntry))
                return Answer(false, true, entryName, exePath);

            if (!Registry.Write(entryName, exePath, out var problem))
            {
                RefusedWrite(problem, logger);
                return Answer(false, true, entryName, exePath);
            }

            WriteWasRefused = false;
            logger?.Information(userEntry == null
                ? "The startup entry for this account was missing, so BakaLoader wrote it again"
                : "ValheimBakaLoader has moved, so the startup entry for this account now points at it");
            return Answer(true, true, entryName, exePath);
        }

        /// <summary>
        /// This PC already starts THIS copy for every account, so this account needs no entry
        /// of its own, and one that names ANOTHER copy is the one thing that cannot stay:
        /// Windows would start this copy for everybody AND that one for this host. Both roads
        /// that reach that state, the save and the launch, clear it the same way here and say
        /// the same thing when Windows will not let them.
        /// <para>
        /// A refused DELETE gets the one line every other refusal gets. The note the card draws
        /// comes from the read, because unlike a refused write this refusal leaves the entry
        /// standing there to be read: <see cref="AccountNote"/> finds it beside a machine entry
        /// naming this copy and answers <see cref="OtherCopyStuckNoticeId"/>.
        /// </para>
        /// </summary>
        /// <returns>Whether the stale entry was really taken away.</returns>
        private bool ClearStaleAccountEntry(
            string entryName, string exePath, string userEntry, ILogger logger)
        {
            if (userEntry == null || Same(userEntry, exePath)) return false;

            var outcome = Registry.Delete(RunKeyHive.CurrentUser, entryName);
            if (outcome == RunKeyDeleteOutcome.Removed)
            {
                logger?.Information(
                    "The startup entry for this account named another copy of BakaLoader, and "
                    + "this PC already starts this one for every account, so it was removed");
                return true;
            }

            if (outcome == RunKeyDeleteOutcome.Refused) RefusedDelete(entryName, logger);

            // NotThere: somebody took it away between the read and the delete, which is the
            // state this road was asking for anyway.
            return false;
        }

        private StartupOutcome TurnOn(
            string entryName, string exePath, string userEntry, string machineEntry, ILogger logger)
        {
            // An entry for every account on this PC that already points at this exe does the
            // job. Writing a second one under this account would leave two entries behind for
            // the switch to have to take away again, and both of them would start BakaLoader.
            if (Same(machineEntry, exePath))
            {
                // What this account's entry must NOT do is go on naming some other copy while
                // the host has just said they mean this one. Until 1.2.5 the road ended on the
                // line above, so that stale entry stayed, unsaid and unremoved, and Windows
                // started this copy for every account AND that one for this account.
                return Answer(
                    ClearStaleAccountEntry(entryName, exePath, userEntry, logger),
                    true, entryName, exePath);
            }

            if (Same(userEntry, exePath)) return Answer(false, true, entryName, exePath);

            if (!Registry.Write(entryName, exePath, out var problem))
            {
                RefusedWrite(problem, logger);
                return Answer(false, true, entryName, exePath);
            }

            WriteWasRefused = false;

            // Writing over an existing name is one of two different things, and the log has to
            // say which. A copy that is GONE from disk is the folder having been moved or
            // renamed. A copy that is still installed is two copies, and the host moving this
            // switch is how they choose between them: calling that "BakaLoader has moved" sends
            // a host hunting for a move that never happened.
            if (userEntry == null)
                logger?.Information("ValheimBakaLoader will now run on Windows startup for this account");
            else if (FileThere(userEntry))
                logger?.Information(
                    "The startup entry for this account was re-pointed from another copy of "
                    + "BakaLoader at {OtherPath}", userEntry);
            else
                logger?.Information(
                    "ValheimBakaLoader has moved, so the startup entry for this account now points at it");

            return Answer(true, true, entryName, exePath);
        }

        private StartupOutcome TurnOff(
            string entryName, string exePath, string userEntry, string machineEntry, ILogger logger)
        {
            var changed = false;

            if (userEntry != null)
            {
                var mine = Registry.Delete(RunKeyHive.CurrentUser, entryName);
                if (mine == RunKeyDeleteOutcome.Removed)
                {
                    changed = true;
                    logger?.Information("ValheimBakaLoader will no longer run on Windows startup for this account");
                }
                else if (mine == RunKeyDeleteOutcome.Refused)
                {
                    // A per account Run key that will not open for writing: a policy, a
                    // security tool, a permission somebody tightened by hand. Rare, and the
                    // switch must not read off over it in silence, which is what it did until
                    // now, with nothing in the log either.
                    RefusedDelete(entryName, logger);
                }

                // NotThere: somebody took it away between the read and the delete, which is
                // the state the switch was asking for anyway.
            }

            // Only an entry that names THIS executable is this install's to take away. One of
            // the same name pointing somewhere else belongs to another copy of BakaLoader, and
            // removing it would turn startup off for a copy whose switch the host never
            // touched. It is left alone, and it is the standing note that names it. Same()
            // answers false for no entry at all, so this is the whole of the machine road.
            if (Same(machineEntry, exePath))
            {
                var machine = Registry.Delete(RunKeyHive.LocalMachine, entryName);
                if (machine == RunKeyDeleteOutcome.Removed)
                {
                    changed = true;
                    logger?.Information("The startup entry for every account on this PC was removed as well");
                }
                else if (machine == RunKeyDeleteOutcome.Refused)
                {
                    // Which on this hive means the run is not elevated.
                    WarnAboutMachineHive(entryName, logger);
                }

                // NotThere: somebody else took it away between the read and the delete.
            }

            return Answer(changed, false, entryName, exePath);
        }

        /// <summary>
        /// What a road hands back: whether it changed anything, and the notes worked out by a
        /// fresh read of both hives.
        /// <para>
        /// Every road ends here on purpose. The card asks <see cref="NotesFor"/> on every open,
        /// and a save that worked its note out any other way would be a sentence the next open
        /// of that card could contradict. Going back to the registry costs two reads, both read
        /// only, and buys the one thing this whole road is for.
        /// </para>
        /// </summary>
        private StartupOutcome Answer(bool changed, bool userPreference, string entryName, string exePath) =>
            new() { Changed = changed, Notes = NotesFor(userPreference, entryName, exePath) };

        /// <summary>
        /// Windows refused the write for this account. The preference is saved either way, so
        /// the switch would read on while nothing started: the refusal is remembered for the
        /// run, and the log gets one line, once, carrying what Windows actually said.
        /// <para>
        /// It is remembered rather than answered here because a refused write leaves nothing on
        /// disk for the read behind the card to find. The note itself comes from
        /// <see cref="AccountNote"/>, like every other one.
        /// </para>
        /// </summary>
        private void RefusedWrite(string problem, ILogger logger)
        {
            WriteWasRefused = true;

            if (WarnedAboutRefusedWrite) return;
            WarnedAboutRefusedWrite = true;
            logger?.Warning(
                "Windows refused to write the startup entry for this account, so the switch "
                + "reads on while nothing will start with Windows: {Problem}",
                string.IsNullOrWhiteSpace(problem) ? "no reason was given" : problem);
        }

        /// <summary>
        /// Windows refused to take this account's entry away. The preference is saved either
        /// way, so the switch reads OFF while Windows goes on starting BakaLoader: the log gets
        /// one line, once, naming the way out that needs neither a run as administrator nor a
        /// restart. The note comes from the read, because the entry is still standing there to
        /// be read.
        /// <para>
        /// The delete answers which of three things happened and not why, so there is no
        /// sentence from Windows to carry here. The way out is the same whatever the reason.
        /// </para>
        /// <para>
        /// The same refusal now reaches this line from the switch ON as well, where the PC
        /// starts this copy for every account and this account's entry goes on naming another
        /// one. So the sentence names what is left standing rather than which way the switch
        /// reads: both roads end with Windows starting the copy that entry names, and both end
        /// at the same way out.
        /// </para>
        /// </summary>
        private void RefusedDelete(string entryName, ILogger logger)
        {
            if (WarnedAboutRefusedDelete) return;
            WarnedAboutRefusedDelete = true;
            logger?.Warning(
                "Windows refused to remove the startup entry {EntryName} for this account, "
                + "so Windows goes on starting the copy of BakaLoader that entry names. "
                + "Turn the BakaLoader entry off in the Startup tab in Task Manager.",
                entryName);
        }

        /// <summary>
        /// And the machine wide one, which on that hive means the run is not elevated. One
        /// line, once, because the refusal does not change and a line repeated on every save is
        /// a line nobody reads.
        /// </summary>
        private void WarnAboutMachineHive(string entryName, ILogger logger)
        {
            if (WarnedAboutMachineHive) return;
            WarnedAboutMachineHive = true;
            logger?.Warning(
                "The startup entry {EntryName} under HKEY_LOCAL_MACHINE could not be removed, "
                + "because it was written while BakaLoader was running as administrator. Run "
                + "BakaLoader as administrator once with the switch off, or turn the BakaLoader "
                + "entry off in the Startup tab in Task Manager.",
                entryName);
        }

        private static bool Same(string registered, string exePath) =>
            registered != null
            && !string.IsNullOrWhiteSpace(exePath)
            && registered.Equals(exePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The application's one <see cref="StartupEntry"/>, and the two facts about this process
    /// it is fed with. Everything worth testing lives on <see cref="StartupEntry"/>; this is
    /// the layer that knows it is running inside a Windows Forms application.
    /// </summary>
    public static class StartupHelper
    {
        /// <inheritdoc cref="StartupEntry.MachineHiveNoticeId"/>
        public const string MachineHiveNoticeId = StartupEntry.MachineHiveNoticeId;

        private static readonly StartupEntry Entry = new(new WindowsRunKeyRegistry());

        /// <summary>
        /// Puts the Run key where the preference says. Called only by a save that MOVED the
        /// switch: the Upkeep card posts every switch on it together, so the key arriving is
        /// not the switch moving, and an ordinary save must write nothing to the registry.
        /// </summary>
        public static StartupOutcome ApplyStartupSetting(bool userPreference, ILogger logger) =>
            Entry.Apply(userPreference, Application.ProductName, Application.ExecutablePath, logger);

        /// <inheritdoc cref="StartupEntry.RepointIfMoved"/>
        public static StartupOutcome RepointMovedEntry(bool userPreference, ILogger logger) =>
            Entry.RepointIfMoved(
                userPreference, Application.ProductName, Application.ExecutablePath, logger);

        /// <inheritdoc cref="StartupEntry.NotesFor"/>
        public static StartupNotes NotesFor(bool userPreference)
        {
            try
            {
                return Entry.NotesFor(
                    userPreference, Application.ProductName, Application.ExecutablePath);
            }
            catch { return StartupNotes.None; }
        }

        /// <summary>
        /// Puts the value a save carried onto the preferences, and answers whether that save
        /// actually MOVED the switch.
        /// <para>
        /// The same rule, and the same reason, as
        /// <see cref="Logging.LogLevelControl.ApplySavedValue"/>: every switch on the Upkeep
        /// card posts the whole card together, so this key arrives on a save that came from a
        /// different switch entirely. Applying on the key's PRESENCE is what made an ordinary
        /// save open the Run key, every time, for a preference nobody had touched.
        /// </para>
        /// </summary>
        public static bool ApplySavedValue(Game.UserPreferences prefs, JToken carried)
        {
            if (prefs == null || carried == null) return false;

            var wanted = carried.Value<bool>();
            var moved = wanted != prefs.StartWithWindows;
            prefs.StartWithWindows = wanted;
            return moved;
        }
    }
}
