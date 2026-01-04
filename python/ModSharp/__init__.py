import clr
import sys

# Ensure Sharp.Shared is referenced
clr.AddReference("Sharp.Shared")

# Import types
from Sharp.Shared.GameEvents import IEventPlayerDeath
from Sharp.Shared.Enums import HudPrintChannel
from Sharp.Shared.GameEntities import IPlayerController

class BasePlugin:
    def __init__(self, shared_system, loader):
        self.shared_system = shared_system
        self.loader = loader

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

# Decorators
def ConsoleCommand(name, description=""):
    def decorator(func):
        # We wrap the function to wrap the player argument
        # The loader calls this method with (player_obj, command_info)
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
        # Handle IPlayerController
        # Python.NET: checks type of internal object
        if self._internal is None:
            return

        # Check for IPlayerController interface or if it has Print method
        # Using hasattr is safer than isinstance check if type isn't fully imported or proxied identically
        if hasattr(self._internal, "Print"):
             # Print(HudPrintChannel channel, string message, ...)
             self._internal.Print(HudPrintChannel.Chat, message, None, None, None, None)
        elif hasattr(self._internal, "ConsolePrint"):
             # Fallback for IGameClient or other types
             self._internal.ConsolePrint(message)

class EventPlayerDeath:
    Name = "player_death"

    def __init__(self, internal_event):
        self._internal = internal_event

    @property
    def Attacker(self):
        try:
            # IEventPlayerDeath property
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
