using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using ModelExplorer.Indexing;

namespace ModelExplorer.App.Thumbnails;

/// <summary>
/// Fills an <see cref="Image"/> with a model's thumbnail.
/// </summary>
/// <remarks>
/// An attached property rather than a per-row view model. The results list is
/// replaced on every keystroke, and wrapping 100k <see cref="ModelFile"/>s in
/// observable tiles each time would allocate more than the search itself. Here
/// the item stays a plain ModelFile and the control asks for its picture.
///
/// This is also what makes recycling work: a recycled container gets a new
/// <see cref="SourceFileProperty"/> value, which cancels the request the row was
/// waiting on before starting the new one.
/// </remarks>
public static class ThumbnailImage
{
    /// <summary>
    /// Set once on the window; inherited by every row. Saves reaching for a
    /// global, and leaves the service owned by the view model that created it.
    /// </summary>
    public static readonly DependencyProperty ServiceProperty =
        DependencyProperty.RegisterAttached(
            "Service",
            typeof(ThumbnailService),
            typeof(ThumbnailImage),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetService(DependencyObject element, ThumbnailService? value) =>
        element.SetValue(ServiceProperty, value);

    public static ThumbnailService? GetService(DependencyObject element) =>
        (ThumbnailService?)element.GetValue(ServiceProperty);

    /// <summary>The model this image should show.</summary>
    public static readonly DependencyProperty SourceFileProperty =
        DependencyProperty.RegisterAttached(
            "SourceFile",
            typeof(ModelFile),
            typeof(ThumbnailImage),
            new PropertyMetadata(null, OnSourceFileChanged));

    public static void SetSourceFile(DependencyObject element, ModelFile? value) =>
        element.SetValue(SourceFileProperty, value);

    public static ModelFile? GetSourceFile(DependencyObject element) =>
        (ModelFile?)element.GetValue(SourceFileProperty);

    /// <summary>The in-flight request, so it can be cancelled when the row moves on.</summary>
    private static readonly DependencyProperty RequestProperty =
        DependencyProperty.RegisterAttached(
            "Request",
            typeof(IDisposable),
            typeof(ThumbnailImage),
            new PropertyMetadata(null));

    private static void OnSourceFileChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Image image)
        {
            return;
        }

        ((IDisposable?)image.GetValue(RequestProperty))?.Dispose();
        image.SetValue(RequestProperty, null);

        image.BeginAnimation(UIElement.OpacityProperty, null);
        image.Source = null;
        image.Opacity = 1;

        if (e.NewValue is not ModelFile file || GetService(image) is not { } service)
        {
            return;
        }

        // Distinguishes a memory-cache hit, which calls back before Load returns,
        // from a real render. Fading in something that was already in hand makes
        // scrolling back over seen rows flicker.
        var settled = false;

        var request = service.Load(file, source =>
        {
            // The row may have been recycled onto a different model between the
            // render finishing and this running.
            if (!ReferenceEquals(GetSourceFile(image), file))
            {
                return;
            }

            image.Source = source;

            if (source is not null && settled)
            {
                image.Opacity = 0;
                image.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(140)))
                    {
                        FillBehavior = FillBehavior.Stop,
                    });

                image.Opacity = 1;
            }
        });

        settled = true;
        image.SetValue(RequestProperty, request);
    }
}
