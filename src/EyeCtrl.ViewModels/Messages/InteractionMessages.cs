namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Models;

// Core -> ViewModel
public record ShowActivationButtonMessage(ScreenPoint Position);
public record HideActivationButtonMessage;
public record ShowActionMenuMessage(ScreenPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack, int DwellTimeMs, int ZoomDisplayRadius = 0, double CircleSize = 250);
public record HideActionMenuMessage;
public record StateChangedMessage(InteractionState NewState);
public record StartZoomMessage(ScreenPoint Center, int Radius, double Magnification);
public record EndZoomMessage;
public record ZoomCloseMessage;
public record ZoomRecaptureMessage;
public record ActionRequestedMessage(InteractionAction Action, ScreenPoint Point, bool IsFromZoom = false);

// ViewModel -> Core
public record ActivationCompletedMessage;
public record ActionSelectedMessage(InteractionAction Action);
public record BackSelectedMessage;
public record PauseToggledMessage;
public record ZoomToggledMessage;
public record KeyboardToggledMessage;
public record SettingsRequestedMessage;
public record KeyActivatedMessage(string Key);
public record PredictionSelectedMessage(string Word);
public record SettingsChangedMessage;
