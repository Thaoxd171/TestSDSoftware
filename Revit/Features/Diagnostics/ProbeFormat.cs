using System;
using System.Text;
using Autodesk.Revit.DB;
using SDSoftware.RevitTest.Extensions;

namespace SDSoftware.RevitTest.Features.Diagnostics
{
    /// <summary>Formatting helpers shared by the diagnostic reports. All lengths are printed in mm.</summary>
    internal static class ProbeFormat
    {
        public static void Section(StringBuilder report, string title, Action body)
        {
            report.AppendLine();
            report.AppendLine("=======================================================================");
            report.AppendLine("  " + title);
            report.AppendLine("=======================================================================");

            try
            {
                body();
            }
            catch (Exception ex)
            {
                report.AppendLine("!! section failed: " + ex.Message);
            }
        }

        public static string Mm(XYZ point)
        {
            return point == null
                ? "(null)"
                : $"({point.X.FeetToMm():0.#}, {point.Y.FeetToMm():0.#}, {point.Z.FeetToMm():0.#})";
        }

        public static string Vec(XYZ vector)
        {
            return vector == null ? "(null)" : $"({vector.X:0.###}, {vector.Y:0.###}, {vector.Z:0.###})";
        }

        public static string BoxSize(BoundingBoxXYZ box)
        {
            if (box == null)
            {
                return "(no bbox)";
            }

            var size = box.Max - box.Min;
            return $"{size.X.FeetToMm():0.#} x {size.Y.FeetToMm():0.#} x {size.Z.FeetToMm():0.#}";
        }

        public static string Describe(Element element)
        {
            if (element == null)
            {
                return "(null)";
            }

            var typeName = element.Document.GetElement(element.GetTypeId())?.Name ?? "?";
            return $"id={element.Id.ToLong()} cat=\"{element.Category?.Name}\" family=\"{FamilyNameOf(element)}\" type=\"{typeName}\"";
        }

        public static string FamilyNameOf(Element element)
        {
            if (element is FamilyInstance instance)
            {
                return instance.Symbol?.FamilyName ?? "?";
            }

            return element.GetTypeAs<ElementType>()?.FamilyName ?? "(system)";
        }

        public static void DumpParameters(StringBuilder report, Element element, string indent = "  ")
        {
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter?.Definition == null)
                {
                    continue;
                }

                var builtIn = parameter.Definition is InternalDefinition internalDefinition
                    ? internalDefinition.BuiltInParameter.ToString()
                    : "(shared)";

                report.AppendLine(
                    $"{indent}{Trim(parameter.Definition.Name, 34),-34} = {Trim(ValueOf(parameter), 34),-34}" +
                    $" [{parameter.StorageType}] {builtIn}");
            }
        }

        public static string ValueOf(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
            {
                return "(none)";
            }

            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return $"{parameter.AsValueString()} ({parameter.AsDouble().FeetToMm():0.##} mm)";
                case StorageType.Integer:
                    return parameter.AsValueString() ?? parameter.AsInteger().ToString();
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.ElementId:
                    return $"id {parameter.AsElementId().ToLong()}";
                default:
                    return "(none)";
            }
        }

        public static string LocationOf(Element element)
        {
            var point = element.GetLocationPoint();
            if (point != null)
            {
                return $"point {Mm(point)}";
            }

            var line = element.GetLocationLine();
            if (line != null)
            {
                return $"line {Mm(line.GetEndPoint(0))} -> {Mm(line.GetEndPoint(1))}";
            }

            return "(no location)";
        }

        public static string OrientationOf(Element element)
        {
            if (element is FamilyInstance instance)
            {
                var rotation = (element.Location as LocationPoint)?.Rotation ?? 0;
                return $"facing={Vec(instance.FacingOrientation)} hand={Vec(instance.HandOrientation)} " +
                       $"rotation={rotation.RadiansToDegrees():0.##} deg  mirrored={instance.Mirrored}";
            }

            return "(not a family instance)";
        }

        public static string LevelNameOf(Document document, Element element)
        {
            return document.GetElement(element.LevelId)?.Name ?? "(none)";
        }

        public static string HostOf(Element element)
        {
            var host = (element as FamilyInstance)?.Host;
            return host == null ? "(none)" : Describe(host);
        }

        public static string NameOf(Document document, ElementId id)
        {
            return id == null || id == ElementId.InvalidElementId
                ? "(none)"
                : document.GetElement(id)?.Name ?? $"id {id.ToLong()}";
        }

        public static string Trim(string value, int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= length ? value : value.Substring(0, length - 1) + "~";
        }
    }
}
