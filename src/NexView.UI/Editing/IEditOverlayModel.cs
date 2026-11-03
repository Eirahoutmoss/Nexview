using System.Collections.ObjectModel;
using System.ComponentModel;
using NexView.Core.Editing;

namespace NexView.UI.Editing;

/// <summary>Düzenleme kaplamasının (EditOverlay) masaüstü ve mobil düzenleme modellerinden beklediği yüzey.</summary>
public interface IEditOverlayModel : INotifyPropertyChanged
{
    bool IsActive { get; }
    bool OverlayActive { get; }
    EditTool Tool { get; }
    NRect CropRect { get; set; }
    double? CropRatioNormalized { get; }
    bool DocumentPerspective { get; }
    NPoint[] Quad { get; set; }
    ObservableCollection<Annotation> Annotations { get; }
    CompareMode Compare { get; }
    double SplitPosition { get; set; }
    AnnotationKind MarkupKind { get; }
    string MarkupColor { get; }
    double MarkupSize { get; }
    Task AddTextAsync(double u, double v);
    void AddAnnotation(Annotation a);
    void CommitInteraction();
}
