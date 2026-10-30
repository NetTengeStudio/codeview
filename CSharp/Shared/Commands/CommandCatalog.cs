using System;
using System.Collections.Generic;

namespace ScpItdMod.Commands {
    public sealed class CommandSpec {
        public readonly string Usage;
        public readonly CommandNode Root;

        public CommandSpec(string usage, CommandNode root) {
            Usage = usage ?? string.Empty;
            Root = root;
        }
    }

    public static class CommandCatalog {
        private static readonly string[] onOff = { "on", "off" };
        private static readonly string[] trueFalse = { "true", "false" };
        private static readonly Dictionary<string, CommandSpec> specs = Build();

        public static bool TryGet(string name, out CommandSpec spec) => specs.TryGetValue(name, out spec);

        private static Dictionary<string, CommandSpec> Build() {
            var table = new Dictionary<string, CommandSpec>(StringComparer.OrdinalIgnoreCase);
            Add(table, "scpitd_motionstats", "scpitd_motionstats", null);
            Add(table, "scpitd_reducedmotion", "scpitd_reducedmotion", null);
            Add(table, "scpitd_motionblur", "scpitd_motionblur [on|off]", CommandNode.Leaf("state", onOff));
            Add(table, "scpitd_frameprof", "scpitd_frameprof", null);
            Add(table, "scpitd_typespeed", "scpitd_typespeed <factor 0.25-4|instant|normal>", CommandNode.Leaf("factor or mode", "instant", "normal", "0.25", "0.5", "1", "2", "4"));
            Add(table, "scpitd_typevolume", "scpitd_typevolume <0..0.15|normal>", CommandNode.Leaf("volume", "0", "normal", "0.04", "0.15"));
            Add(table, "scpitd_invtrace", "scpitd_invtrace", null);
            Add(table, "scpitd_invstate", "scpitd_invstate", null);
            Add(table, "scpitd_menuinstall", "scpitd_menuinstall", null);
            Add(table, "scpitd_pcstats", "scpitd_pcstats", null);
            Add(table, "scpitd_roster", "scpitd_roster", null);
            Add(table, "scpitd_who", "scpitd_who", null);
            Add(table, "scpitd_recordvoice", "scpitd_recordvoice <true|false>", CommandNode.Leaf("allowed", trueFalse));
            Add(table, "scpitd_memory", "scpitd_memory", null);
            Add(table, "scpitd_uicapture", "scpitd_uicapture [frames 1..600] [stride 1..600]", new CommandNode("frames 1..600", null, null, CommandNode.Leaf("stride 1..600")));
            Add(table, "scpitd_cardgrant", "scpitd_cardgrant <card item id> <tag>", new CommandNode("card item id", null, null, CommandNode.Leaf("tag")));
            Add(table, "scpitd_cardrevoke", "scpitd_cardrevoke <card item id> <tag>", new CommandNode("card item id", null, null, CommandNode.Leaf("tag")));
            Add(table, "scpitd_voluntarydeath", "scpitd_voluntarydeath [on|off]", CommandNode.Leaf("state", onOff));
            Add(table, "scpitd_meteo", "scpitd_meteo", null);
            Add(table, "scpitd_pc", "scpitd_pc", null);
            Add(table, "scpitd_people", "scpitd_people", null);
            Add(table, "scpitd_profilelock", "scpitd_profilelock <on|off>", CommandNode.Leaf("state", onOff));
            Add(table, "scpitd_poolstats", "scpitd_poolstats", null);
            Add(table, "scpitd_cams", "scpitd_cams", null);
            Add(table, "scpitd_cine_list", "scpitd_cine_list", null);
            Add(table, "scpitd_cine_start", "scpitd_cine_start <scene> [binding=id ...] [audience=characterId,...]", CommandNode.Leaf("scene"));
            Add(table, "scpitd_cine_trigger", "scpitd_cine_trigger <signal>", CommandNode.Leaf("signal"));
            Add(table, "scpitd_cine_stop", "scpitd_cine_stop <instance>", CommandNode.Leaf("instance"));
            Add(table, "scpitd_cine_skip", "scpitd_cine_skip [instance]", CommandNode.Leaf("instance"));
            Add(table, "scpitd_cine_skipall", "scpitd_cine_skipall <instance>", CommandNode.Leaf("instance"));
            Add(table, "scpitd_cine_signal", "scpitd_cine_signal <instance> <event>", new CommandNode("instance", null, null, CommandNode.Leaf("event")));
            Add(table, "scpitd_cine_reload", "scpitd_cine_reload", null);
            Add(table, "scpitd_cine_status", "scpitd_cine_status", null);
            Add(table, "scpitd_net_trace", "scpitd_net_trace <on|off|dump>", CommandNode.Leaf("action", "on", "off", "dump"));
            Add(table, "scpitd_profile", "scpitd_profile start [scenario] [seconds<=120] | stop | dump | frame | mark [event] | status",
                CommandNode.Branch("action", new[] { "start", "stop", "dump", "frame", "mark", "status" }, null,
                    CommandNode.Word("start", new CommandNode("scenario", null, null, CommandNode.Leaf("seconds <= 120"))),
                    CommandNode.Word("mark", CommandNode.Leaf("event"))));
            Add(table, "scpitd_invcheck", "scpitd_invcheck", null);
            Add(table, "scpitd_emote_trace", "scpitd_emote_trace", null);
            Add(table, "scpitd_status", "scpitd_status", null);
            Add(table, "cold", "cold status|on|off|toggle|map air|map surface|map off|trace start|trace stop",
                CommandNode.Branch("action", new[] { "status", "on", "off", "toggle", "map", "trace" }, null,
                    CommandNode.Word("map", CommandNode.Leaf("map mode", "air", "surface", "off")),
                    CommandNode.Word("trace", CommandNode.Leaf("trace", "start", "stop"))));
            Add(table, "scpitd_cold", "scpitd_cold [on|off|toggle]", CommandNode.Leaf("state", "on", "off", "toggle"));
            Add(table, "weather", "weather set|wind|temp|time|season|timescale|snowdepth|freeze|forecast|dump|quality|dig|hulls|cold",
                CommandNode.Branch("subcommand", new[] { "set", "wind", "temp", "time", "season", "timescale", "snowdepth", "freeze", "forecast", "dump", "quality", "dig", "hulls", "cold" }, null,
                    CommandNode.Word("set", CommandNode.Branch("regime", new[] { "Clear", "Snowfall", "Drifting", "Blizzard", "Storm", "Recovery" }, CommandNode.Leaf("seconds"))),
                    CommandNode.Word("wind", new CommandNode("speed m/s", null, null, CommandNode.Leaf("direction deg"))),
                    CommandNode.Word("temp", CommandNode.Leaf("celsius -80..-15")),
                    CommandNode.Word("time", new CommandNode("hour 0..23.999 or hh:mm", null, null, CommandNode.Leaf("seconds"))),
                    CommandNode.Word("season", CommandNode.Leaf("season", "polar_day", "polar_night", "equinox")),
                    CommandNode.Word("timescale", CommandNode.Leaf("factor 1..10080")),
                    CommandNode.Word("snowdepth", CommandNode.Leaf("metres")),
                    CommandNode.Word("freeze", CommandNode.Leaf("state", onOff)),
                    CommandNode.Word("forecast", CommandNode.Leaf("hours 1..48")),
                    CommandNode.Word("quality", CommandNode.Leaf("quality 0.1..1")),
                    CommandNode.Word("cold", CommandNode.Branch("action", new[] { "status", "on", "off", "toggle", "trace" }, null,
                        CommandNode.Word("trace", CommandNode.Leaf("trace", "start", "stop"))))));
            Add(table, "scpitdambient", "scpitdambient [true|false]", CommandNode.Leaf("value", trueFalse));
            Add(table, "generalambient", "generalambient [true|false]", CommandNode.Leaf("value", trueFalse));
            Add(table, "ambientzone", "ambientzone [general|interior|machinery|outpost|none]", CommandNode.Leaf("zone", "general", "interior", "machinery", "outpost", "none"));
            Add(table, "audiofollowdevice", "audiofollowdevice [true|false]", CommandNode.Leaf("value", trueFalse));
            Add(table, "alwaysblinkenable", "alwaysblinkenable [true|false]", CommandNode.Leaf("value", trueFalse));
            Add(table, "respawninterval", "respawninterval <seconds>", CommandNode.Leaf("seconds"));
            Add(table, "respawnratio", "respawnratio <0..1>", CommandNode.Leaf("ratio 0..1", "0", "0.5", "1"));
            return table;
        }

        private static void Add(Dictionary<string, CommandSpec> table, string name, string usage, CommandNode root) {
            table[name] = new CommandSpec(usage, root);
        }
    }
}
