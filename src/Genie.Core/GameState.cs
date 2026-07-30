using System.Collections.Concurrent;

namespace Genie.Core.Models;

// ── DR domain enumerations (from Elanthipedia) ───────────────────────────────

public enum DrRace
{
    Human, Elf, Elothean, Dwarf, Gnome, Halfling, GorTog,
    Kaldar, Prydaen, Rakash, SKraMur, Unknown
}

public enum DrGuild
{
    Barbarian, Bard, Cleric, Empath, MoonMage, Paladin,
    Ranger, Thief, Trader, WarriorMage, Necromancer,
    Unknown
}

public enum Stance { Offensive, Advance, Forward, Neutral, Guarded, Defensive, Unknown }

public enum CharacterStatus
{
    Normal, Kneeling, Prone, Sitting, Stunned, Webbed,
    Bleeding, Poisoned, Diseased, Hidden, Invisible, Joined, Dead
}

// ── Core character attributes (8 DR stats from Elanthipedia) ────────────────

public sealed class Attributes
{
    public int Strength    { get; set; }
    public int Reflex      { get; set; }
    public int Agility     { get; set; }
    public int Charisma    { get; set; }
    public int Discipline  { get; set; }
    public int Wisdom      { get; set; }
    public int Intelligence{ get; set; }
    public int Stamina     { get; set; }
}

// ── Vitals (percentage bars from progressBar XML) ────────────────────────────

public sealed class Vitals
{
    public int Health         { get; set; } = 100;  // 0–100
    public int Mana           { get; set; } = 100;
    public int Spirit         { get; set; } = 100;
    public int StaminaFatigue { get; set; } = 100;  // "stamina" bar
    public int Concentration  { get; set; } = 100;  // spell prep bar
    public int Encumbrance    { get; set; } = 0;    // 0 = unencumbered
}

// ── Skill rank record ────────────────────────────────────────────────────────

public sealed class SkillRank
{
    public string Name      { get; set; } = "";
    public int    Rank      { get; set; }
    public int    LearningPct { get; set; }  // 0–34 (DR learning states)
}

// ── Room state ───────────────────────────────────────────────────────────────

public sealed class RoomState
{
    public string   Title       { get; set; } = "";
    public string   Description { get; set; } = "";
    public string   Exits       { get; set; } = "";
    public string   Objects     { get; set; } = "";
    public string   Players     { get; set; } = "";
    /// <summary>Bold creature phrases in the room (from <c>room objs</c>),
    /// after the monster-count ignore list. Backs <c>$monsterlist</c>.</summary>
    public IReadOnlyList<string> Creatures { get; set; } = System.Array.Empty<string>();
    /// <summary>Number of creatures in the room after the ignore list
    /// (Genie 4 <c>$monstercount</c>).</summary>
    public int      MonsterCount { get; set; }
    /// <summary>Mapper room ID (from GenieClient/Maps data or &lt;nav rm="..."/&gt;).</summary>
    public int      RoomId      { get; set; }
    /// <summary>Space-separated exit directions from &lt;compass&gt; (e.g. "north south east").</summary>
    public string   CompassExits { get; set; } = "";
    /// <summary>DR room/scene art id from &lt;resource picture="..."/&gt;. Empty when
    /// the room has no artwork ("0"). Backs the Scene panel (App, gated by
    /// <c>showimages</c>).</summary>
    public string   ImageId     { get; set; } = "";
}

// ── Injuries ─────────────────────────────────────────────────────────────────

/// <summary>
/// Latest injuries-dialog reading for one body region. <see cref="Kind"/> /
/// <see cref="Severity"/> mirror the last <c>&lt;image&gt;</c> update the server
/// sent for the region under the player's current injury display mode
/// (E/I Wound/Scar/Both — see <see cref="Events.InjuryEvent"/>).
/// </summary>
public readonly record struct InjuryReading(Events.InjuryKind Kind, int Severity);

// ── Combat state ─────────────────────────────────────────────────────────────

public sealed class CombatState
{
    public string           Target      { get; set; } = "";
    public DateTimeOffset   RoundTimeEnd{ get; set; }
    public DateTimeOffset   CastTimeEnd { get; set; }
    public string           PreparedSpell { get; set; } = "";
    /// <summary>Wall-clock instant the current spell was prepared (null = none).
    /// Backs the Genie 4 <c>$spelltime</c> countup.</summary>
    public DateTimeOffset?  SpellTimeStart { get; set; }
    public Stance           Stance      { get; set; } = Stance.Neutral;
    public bool             InRoundTime => DateTimeOffset.UtcNow < RoundTimeEnd;
    public bool             InCastTime  => DateTimeOffset.UtcNow < CastTimeEnd;
    public double           RoundTimeRemaining =>
        Math.Max(0, (RoundTimeEnd - DateTimeOffset.UtcNow).TotalSeconds);

    /// <summary>Seconds since the current spell was prepared (Genie 4
    /// <c>$spelltime</c>); 0 when no spell is prepared.</summary>
    public double           SpellTimeSeconds =>
        SpellTimeStart is { } t ? Math.Max(0, (DateTimeOffset.UtcNow - t).TotalSeconds) : 0;

    /// <summary>Latest per-creature combat status from <c>&lt;crtrStatus&gt;</c>,
    /// keyed by the creature's <c>exist</c> object id (public #202). Cleared on
    /// room change — engagement is room-local.</summary>
    public ConcurrentDictionary<string, CreatureStatusReading> CreatureStatuses { get; } = new();
}

/// <summary>Per-creature combat flags from <c>&lt;crtrStatus&gt;</c>. <see cref="Disengaged"/>
/// is you relative to the creature (false = engaged).</summary>
public readonly record struct CreatureStatusReading(bool Hostile, bool Disengaged, bool Flying);

// ── Inventory ────────────────────────────────────────────────────────────────

public sealed class InventoryState
{
    /// <summary>Full display name of the held item ("razor-edged scimitar"),
    /// from the &lt;left&gt;/&lt;right&gt; body text — Genie 4's <c>$lefthand</c>/
    /// <c>$righthand</c> (#172). "Empty" when the hand is empty.</summary>
    public string LeftHand  { get; set; } = "";
    public string RightHand { get; set; } = "";
    /// <summary>Bare noun from the <c>noun</c> attribute ("scimitar") —
    /// Genie 4's <c>$lefthandnoun</c>/<c>$righthandnoun</c>.</summary>
    public string LeftHandNoun  { get; set; } = "";
    public string RightHandNoun { get; set; } = "";
    public string LeftExistId  { get; set; } = "";
    public string RightExistId { get; set; } = "";
}

// ── Full character/world snapshot ────────────────────────────────────────────

/// <summary>
/// The canonical live game state.  Updated by <see cref="GameStateEngine"/> as
/// parsed events arrive.  Thread-safe for reading from UI / AI threads.
/// </summary>
public sealed class GameState
{
    // Character identity
    public string    CharacterName { get; set; } = "";
    public DrRace    Race          { get; set; } = DrRace.Unknown;
    public DrGuild   Guild         { get; set; } = DrGuild.Unknown;
    /// <summary>Raw guild display name from the <c>info</c> verb (e.g.
    /// "Barbarian", "Moon Mage", "Commoner"). May hold values outside the
    /// <see cref="DrGuild"/> enum (un-guilded characters report "Commoner").</summary>
    public string    GuildName     { get; set; } = "";
    public int       Circle        { get; set; }  // character circle (level equivalent)

    // Core DR stats
    public Attributes  Attributes { get; } = new();
    public Vitals      Vitals     { get; } = new();
    public SkillRank[] Skills     { get; set; } = [];

    /// <summary>
    /// Live skill ranks indexed by name — populated by the parser hook
    /// for &lt;component id='exp X'&gt; events. Used by the AutoMapper's
    /// weighted Dijkstra to filter exits the character can't take.
    /// Separate from <see cref="Skills"/> (which is a one-shot snapshot
    /// from `info`/`skills` parsing) — this is the live stream that
    /// updates per-skill as the engine sees rank changes.
    /// </summary>
    public Skills.SkillStore LiveSkills { get; } = new();

    // World
    public RoomState    Room      { get; } = new();
    public CombatState  Combat    { get; } = new();
    public InventoryState Inventory { get; } = new();

    // Status flags (from indicators)
    public HashSet<CharacterStatus> ActiveStatuses { get; } = [];

    /// <summary>
    /// Current injuries by body region (raw dialog region id — "head",
    /// "rightArm", "nsys", …). A region reads healthy when absent OR when its
    /// entry is <see cref="Events.InjuryKind.None"/> — the engine keeps None
    /// entries so consumers can tell "healed" from "never reported".
    /// </summary>
    public ConcurrentDictionary<string, InjuryReading> Injuries { get; } = new();

    // Misc UI state
    public string ActiveStream   { get; set; } = "main";
    public DateTimeOffset LastPrompt { get; set; }

    // Raw component text (all received component IDs → latest content)
    public ConcurrentDictionary<string, string> Components { get; } = new();

    /// <summary>
    /// Clear the snapshot back to defaults <b>in place</b>, preserving the identity
    /// of every nested object (<see cref="Attributes"/>, <see cref="Vitals"/>,
    /// <see cref="Room"/>, <see cref="Combat"/>, <see cref="Inventory"/>,
    /// <see cref="LiveSkills"/>, <see cref="ActiveStatuses"/>, <see cref="Components"/>).
    /// The persistent core holds these by reference across reconnect, so we mutate
    /// rather than replace. Called at the start of each connect.
    ///
    /// <para><paramref name="clearPerCharacter"/> distinguishes a genuine character
    /// SWITCH (true → also drop the previous character's identity + live skill ranks)
    /// from a same-character reconnect or the first connect from offline (false →
    /// keep <see cref="LiveSkills"/> + guild/circle so the Mapper doesn't re-prompt
    /// for <c>info</c>/<c>exp</c> and pathfinding weights survive). The transient
    /// world/vitals/combat state always resets — it repopulates from the new
    /// session immediately.</para>
    /// </summary>
    public void Reset(bool clearPerCharacter = true)
    {
        // Per-character data — only dropped on a real character switch. Live skill
        // ranks (and guild/circle) don't auto-refresh on connect (they need
        // info/exp), so clearing them on a same-char reconnect would force the user
        // to re-fetch and re-trigger the Mapper's skills prompt.
        if (clearPerCharacter)
        {
            CharacterName = "";
            Race          = DrRace.Unknown;
            Guild         = DrGuild.Unknown;
            GuildName     = "";
            Circle        = 0;
            Skills        = [];
            LiveSkills.Clear();
        }

        // Core stats (transient — refresh from the new session).
        Attributes.Strength = Attributes.Reflex = Attributes.Agility = Attributes.Charisma =
            Attributes.Discipline = Attributes.Wisdom = Attributes.Intelligence = Attributes.Stamina = 0;
        Vitals.Health = Vitals.Mana = Vitals.Spirit = Vitals.StaminaFatigue = Vitals.Concentration = 100;
        Vitals.Encumbrance = 0;

        // World
        Room.Title = Room.Description = Room.Exits = Room.Objects = Room.Players =
            Room.CompassExits = Room.ImageId = "";
        Room.Creatures    = System.Array.Empty<string>();
        Room.MonsterCount = 0;
        Room.RoomId       = 0;

        Combat.Target = Combat.PreparedSpell = "";
        Combat.RoundTimeEnd  = default;
        Combat.CastTimeEnd   = default;
        Combat.SpellTimeStart = null;
        Combat.Stance        = Stance.Neutral;
        Combat.CreatureStatuses.Clear();

        Inventory.LeftHand = Inventory.RightHand = Inventory.LeftHandNoun = Inventory.RightHandNoun =
            Inventory.LeftExistId = Inventory.RightExistId = "";

        // Status / misc
        ActiveStatuses.Clear();
        Injuries.Clear();
        Components.Clear();
        ActiveStream = "main";
        LastPrompt   = default;
    }
}
