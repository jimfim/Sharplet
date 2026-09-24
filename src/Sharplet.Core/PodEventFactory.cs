using k8s.Models;

namespace Sharplet.Core;

/// <summary>
/// Builds the <see cref="Corev1Event"/> objects the kubelet records for pods. The event
/// watcher (Normal events) and the provider-error reporter (Warning events) share this so
/// every event carried an identical involved-object identity for the pod it belongs to.
/// </summary>
internal static class PodEventFactory
{
    public static Corev1Event CreatePodEvent(V1Pod pod, string generateName, string reason, string message,
        string type, string reportingComponent)
    {
        return new Corev1Event
        {
            InvolvedObject = new V1ObjectReference
            {
                ApiVersion = pod.ApiVersion,
                FieldPath = string.Empty,
                Kind = pod.Kind,
                Name = pod.Name(),
                NamespaceProperty = pod.Namespace(),
                ResourceVersion = pod.ResourceVersion(),
                Uid = pod.Uid(),
            },
            Metadata = new V1ObjectMeta { GenerateName = generateName },
            Reason = reason,
            ReportingComponent = reportingComponent,
            Message = message,
            Type = type,
        };
    }
}