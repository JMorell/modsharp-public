import clr
import sys

# Ensure Sharp.Shared is referenced
clr.AddReference("Sharp.Shared")

# Import types
from Sharp.Shared.GameEvents import IEventPlayerDeath
from Sharp.Shared.Enums import HudPrintChannel, TimerAction, GameTimerFlags
from Sharp.Shared.GameEntities import IPlayerController

class BasePlugin:
    def __init__(self, shared_system, loader):
        self.shared_system = shared_system
        self.loader = loader
        self._modsharp = self.shared_system.GetModSharp()

    def Load(self, hot_reload):
        pass

    def Unload(self):
        pass

    def RegisterEventHandler(self, event_type, callback):
        event_name = event_type.Name

        def wrapper(ev, info):
            wrapped_event = event_type(ev)
            return callback(wrapped_event, info)

        self.loader.HookEvent(event_name, wrapper)

    def CreateTimer(self, interval, callback, flags=GameTimerFlags.None):
        """
        Creates a timer.
        callback: function returning TimerAction (Stop/Continue)
        """
        # We might need to wrap the callback to ensure it returns a typed TimerAction
        # Python functions returning 'None' might confuse C# Func<TimerAction>
        # C# expects explicitly TimerAction enum.

        def wrapper():
            result = callback()
            if result is None:
                return TimerAction.Continue
            return result

        return self._modsharp.PushTimer(wrapper, interval, flags)

# Decorators
def ConsoleCommand(name, description=""):
    def decorator(func):
        def wrapper(self, player_obj, info):
            wrapped_player = Player(player_obj)
            return func(self, wrapped_player, info)

        wrapper._modsharp_console_command = type("CommandMeta", (), {"name": name, "description": description})
        return wrapper
    return decorator

class Player:
    def __init__(self, internal):
        self._internal = internal

    def PrintToChat(self, message):
        if self._internal is None:
            return

        if hasattr(self._internal, "Print"):
             self._internal.Print(HudPrintChannel.Chat, message, None, None, None, None)
        elif hasattr(self._internal, "ConsolePrint"):
             self._internal.ConsolePrint(message)

class EventPlayerDeath:
    Name = "player_death"

    def __init__(self, internal_event):
        self._internal = internal_event

    @property
    def Attacker(self):
        try:
            val = self._internal.KillerController
            if val is not None:
                return Player(val)
            return None
        except AttributeError:
            return None

    @property
    def Userid(self):
        try:
            val = self._internal.VictimController
            if val is not None:
                return Player(val)
            return None
        except AttributeError:
            return None
