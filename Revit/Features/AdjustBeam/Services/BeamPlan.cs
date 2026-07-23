using System.Collections.Generic;
using System.Linq;
using SDSoftware.RevitTest.Features.AdjustBeam.Models;

namespace SDSoftware.RevitTest.Features.AdjustBeam.Services
{
    /// <summary>
    /// One beam and what was decided for each of its ends. The two are kept together because a beam is
    /// written in one go - see <see cref="BeamAdjuster"/> - and because the check afterwards needs the
    /// geometry the plan was measured from.
    /// </summary>
    public class BeamPlan
    {
        public BeamPlan(BeamGeometry geometry, IList<BeamEndPlan> ends)
        {
            Geometry = geometry;
            Ends = ends;
        }

        public BeamGeometry Geometry { get; }

        /// <summary>One entry per end, in end order.</summary>
        public IList<BeamEndPlan> Ends { get; }

        public bool HasMove => Ends.Any(end => end.WillMove);

        /// <summary>True when the beam has an end to move, a wedge to cut, or both.</summary>
        public bool NeedsWork => Ends.Any(end => end.WillMove || end.NeedsCut);
    }
}
