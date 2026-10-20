using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace EdgeAlignInspect
{
	public sealed class TemplateMatchQuickTestResult
	{
		public bool Found { get; set; }

		public double Score { get; set; }

		public double Row { get; set; }

		public double Col { get; set; }

		public double Angle { get; set; }

		public RotRectF TemplateRoiCur { get; set; }

		public RotRectF TemplateSearchRoiCur { get; set; }

		public List<PointF> MatchContourPoints { get; set; } = new List<PointF>();

		public string Message { get; set; }
	}

	public sealed class TemplateEdgeInspectProcessor : IDisposable
	{
		private struct PtD
		{
			public double X;

			public double Y;

			public PtD(double x, double y)
			{
				X = x;
				Y = y;
			}    
		}

		private sealed class ContourCandidate
		{
			public List<PointF> Points = new List<PointF>();

			public double Length;

			public double Coverage;

			public double Score => Length + Coverage * 0.05;
		}

		private sealed class FeatureSegment
		{
			public List<PointF> Points = new List<PointF>();

			public double Length;

			public double Score => Length;
		}

		private sealed class EdgePointSample
		{
			public int CandidateIndex;

			public int PointIndex;

			public PointF Point;

			public double U;

			public double V;
		}

		private sealed class SignedLine
		{
			private readonly PointF _p1;

			public double NRow { get; }

			public double NCol { get; }

			private SignedLine(PointF p1, double nRow, double nCol)
			{
				_p1 = p1;
				double num = Math.Sqrt(nRow * nRow + nCol * nCol);
				if (num < 1E-09)
				{
					num = 1.0;
				}
				NRow = nRow / num;
				NCol = nCol / num;
			}

			public static SignedLine FromLine(PointF a, PointF b)
			{
				double num = b.X - a.X;
				double num2 = b.Y - a.Y;
				double num3 = Math.Sqrt(num * num + num2 * num2);
				if (num3 < 1E-09)
				{
					num3 = 1.0;
				}
				num /= num3;
				num2 /= num3;
				return new SignedLine(a, num, 0.0 - num2);
			}

			public double SignedDistance(PointF p)
			{
				return (double)(p.Y - _p1.Y) * NRow + (double)(p.X - _p1.X) * NCol;
			}
		}

		public TemplateTeachData Teach(Bitmap refBmp, EdgeInspectJob job)
		{
			if (refBmp == null)
			{
				throw new ArgumentNullException("refBmp");
			}
			if (job == null)
			{
				throw new ArgumentNullException("job");
			}
			job = job.DeepClone();
			job.Normalize();
			TemplateTeachData templateTeachData = new TemplateTeachData();
			if (!job.Match.Enabled)
			{
				return templateTeachData;
			}
			if (job.TemplateRoi.IsEmpty)
			{
				throw new InvalidOperationException("未设置模板ROI。");
			}
			if (job.Match.UseOuterContourOnly)
			{
				List<PointF> featurePoints = (job.TeachData != null && job.TeachData.FeaturePoints != null && job.TeachData.FeaturePoints.Count > 0) ? new List<PointF>(job.TeachData.FeaturePoints) : ExtractOuterContourPoints(refBmp, job);
				TemplateTeachData teachData = TeachFromOuterContourPoints(refBmp, job, featurePoints);
				teachData.EraseStrokes = CloneEraseStrokes(job.TeachData?.EraseStrokes);
				return teachData;
			}
			HImage hImage = null;
			GCHandle grayHandle = default(GCHandle);
			HObject rect = null;
			HObject imageReduced = null;
			HTuple modelID = null;
			try
			{
				hImage = BitmapToGrayHImage(refBmp, out var _, out var _, out var _, out grayHandle);
				GenRectangle2(job.TemplateRoi, out rect);
				HOperatorSet.ReduceDomain(hImage, rect, out imageReduced);
				HOperatorSet.CreateShapeModel(imageReduced, job.Match.NumLevels, job.Match.AngleStart, job.Match.AngleExtent, "auto", "auto", "use_polarity", 20, 10, out modelID);
				ResolveReferenceMatchPose(hImage, modelID, job, out var refRow, out var refCol, out var refAngle);
				templateTeachData.RefRow = refRow;
				templateTeachData.RefCol = refCol;
				templateTeachData.RefAngle = refAngle;
				templateTeachData.HasTemplate = true;
				templateTeachData.ModelBytes = ExportShapeModelToBytes(modelID);
				templateTeachData.EraseStrokes = CloneEraseStrokes(job.TeachData?.EraseStrokes);
				return templateTeachData;
			}
			finally
			{
				if (modelID != null && modelID.Length > 0)
				{
					try
					{
						HOperatorSet.ClearShapeModel(modelID);
					}
					catch
					{
					}
				}
				imageReduced?.Dispose();
				rect?.Dispose();
				hImage?.Dispose();
				if (grayHandle.IsAllocated)
				{
					grayHandle.Free();
				}
			}
		}

		public List<PointF> ExtractOuterContourPoints(Bitmap refBmp, EdgeInspectJob job)
		{
			if (refBmp == null)
			{
				throw new ArgumentNullException("refBmp");
			}
			if (job == null)
			{
				throw new ArgumentNullException("job");
			}
			job = job.DeepClone();
			job.Normalize();
			if (job.TemplateRoi.IsEmpty)
			{
				throw new InvalidOperationException("未设置模板ROI。");
			}
			HImage hImage = null;
			GCHandle grayHandle = default(GCHandle);
			HObject rect = null;
			HObject imageReduced = null;
			HObject edges = null;
			HObject selectedEdges = null;
			try
			{
				hImage = BitmapToGrayHImage(refBmp, out var _, out var _, out var _, out grayHandle);
				GenRectangle2(job.TemplateRoi, out rect);
				HOperatorSet.ReduceDomain(hImage, rect, out imageReduced);
				double low = Math.Max(1.0, job.Match.EdgeLowThreshold);
				double high = Math.Max(low + 1.0, job.Match.EdgeHighThreshold);
				HOperatorSet.EdgesSubPix(imageReduced, out edges, "canny", Math.Max(0.2, job.Match.EdgeSigma), low, high);
				HOperatorSet.SelectShapeXld(edges, out selectedEdges, "contlength", "and", Math.Max(6.0, job.Match.FeatureMinDistancePx * 1.5), 999999);
				return SelectOuterContourPoints(selectedEdges, job.TemplateRoi, job.Match.FeatureMinDistancePx, job.Match.FeatureAngleBins);
			} 
			finally
			{
				selectedEdges?.Dispose();
				edges?.Dispose();
				imageReduced?.Dispose();
				rect?.Dispose();
				hImage?.Dispose();
				if (grayHandle.IsAllocated)
				{
					grayHandle.Free();
				}
			}
		}   
		public TemplateTeachData TeachFromOuterContourPoints(Bitmap refBmp, EdgeInspectJob job, IList<PointF> featurePoints)
		{
			if (refBmp == null)
			{
				throw new ArgumentNullException("refBmp");
			}
			if (job == null)
			{
				throw new ArgumentNullException("job");
			}
			job = job.DeepClone();
			job.Normalize();
			if (job.TemplateRoi.IsEmpty)
			{
				throw new InvalidOperationException("未设置模板ROI。");
			}
			List<PointF> points = NormalizeTemplateFeaturePoints(featurePoints, job.TemplateRoi, 0.0);
			if (CountRealPoints(points) < 12)
			{
				throw new InvalidOperationException("外轮廓特征点不足，无法创建模板模型。请调整模板ROI、边缘阈值，或减少擦除范围。");
			}
			HImage hImage = null;
			GCHandle grayHandle = default(GCHandle);
			HObject contour = null;
			HTuple modelID = null;
			try
			{
				hImage = BitmapToGrayHImage(refBmp, out var _, out var _, out var _, out grayHandle);
				GenContourFromPoints(points, out contour);
				HOperatorSet.CreateShapeModelXld(contour, job.Match.NumLevels, job.Match.AngleStart, job.Match.AngleExtent, "auto", "auto", "ignore_local_polarity", 20, out modelID);
				SetXldModelOriginToTemplateRoiCenter(modelID, points, job.TemplateRoi);
				ResolveReferenceMatchPose(hImage, modelID, job, out var refRow, out var refCol, out var refAngle);
				TemplateTeachData teachData = new TemplateTeachData
				{
					HasTemplate = true,
					ModelBytes = ExportShapeModelToBytes(modelID),
					FeaturePoints = points,
					EraseStrokes = CloneEraseStrokes(job.TeachData?.EraseStrokes),
					RefRow = refRow,
					RefCol = refCol,
					RefAngle = refAngle
				};
				return teachData;
			}
			finally
			{
				if (modelID != null && modelID.Length > 0)
				{
					try
					{
						HOperatorSet.ClearShapeModel(modelID);
					}
					catch
					{
					}
				}
				contour?.Dispose();
				hImage?.Dispose();
				if (grayHandle.IsAllocated)
				{
					grayHandle.Free();
				}
			}
		}

		private static List<PointF> SelectOuterContourPoints(HObject edges, RotRectF roi, double minDistance, int maxPointHint)
		{
			List<PointF> empty = new List<PointF>();
			if (edges == null || roi.IsEmpty)
			{
				return empty;
			}
			PointF axisU = roi.GetAxisU();
			PointF axisV = roi.GetAxisV();
			List<ContourCandidate> candidates = new List<ContourCandidate>();
			HOperatorSet.CountObj(edges, out var countTuple);
			int count = countTuple.Length > 0 ? countTuple[0].I : 0;
			for (int i = 1; i <= count; i++)
			{
				HObject contour = null;
				try
				{
					HOperatorSet.SelectObj(edges, out contour, i);
					HOperatorSet.GetContourXld(contour, out var rows, out var cols);
					ContourCandidate candidate = BuildContourCandidate(rows, cols, roi, axisU, axisV);
					if (candidate.Points.Count >= 4 && candidate.Length >= Math.Max(8.0, minDistance * 2.0))
					{
						candidates.Add(candidate);
					}
				}
				finally
				{
					contour?.Dispose();
				}
			}
			if (candidates.Count == 0)
			{
				return empty;
			}
			int maxTotalPoints = Math.Max(20000, maxPointHint <= 0 ? 20000 : maxPointHint * 20);
			double modelPointDistance = Math.Max(0.5, minDistance * 0.15);
			return SelectHalconPreviewStylePoints(candidates, modelPointDistance, maxTotalPoints);
		}

		private static List<PointF> SelectHalconPreviewStylePoints(List<ContourCandidate> candidates, double minDistance, int maxTotalPoints)
		{
			if (candidates == null || candidates.Count == 0)
			{
				return new List<PointF>();
			}
			List<FeatureSegment> segments = candidates
				.Where(c => c.Points != null && c.Points.Count >= 2)
				.OrderByDescending(c => c.Score)
				.Select(c => new FeatureSegment
				{
					Points = c.Points,
					Length = c.Length
				})
				.ToList();
			return BuildProportionalPointListFromSegments(segments, minDistance, maxTotalPoints);
		}

		private static List<PointF> SelectFirstEdgeFromRoiSidesSegments(List<ContourCandidate> candidates, RotRectF roi, PointF axisU, PointF axisV, double minDistance, int maxTotalPoints)
		{
			List<PointF> empty = new List<PointF>();
			if (candidates == null || candidates.Count == 0 || roi.IsEmpty)
			{
				return empty;
			}
			double binSize = Math.Max(2.0, minDistance);
			int uBins = Math.Max(16, Math.Min(2000, (int)Math.Ceiling(roi.HalfLen1 * 2.0 / binSize) + 1));
			int vBins = Math.Max(16, Math.Min(2000, (int)Math.Ceiling(roi.HalfLen2 * 2.0 / binSize) + 1));
			double[] topDist = CreateBestDistanceArray(uBins);
			double[] bottomDist = CreateBestDistanceArray(uBins);
			double[] rightDist = CreateBestDistanceArray(vBins);
			double[] leftDist = CreateBestDistanceArray(vBins);
			long[] topKey = CreateBestKeyArray(uBins);
			long[] bottomKey = CreateBestKeyArray(uBins);
			long[] rightKey = CreateBestKeyArray(vBins);
			long[] leftKey = CreateBestKeyArray(vBins);
			List<EdgePointSample> samples = new List<EdgePointSample>();

			for (int ci = 0; ci < candidates.Count; ci++)
			{
				ContourCandidate candidate = candidates[ci];
				for (int pi = 0; pi < candidate.Points.Count; pi++)
				{
					PointF pt = candidate.Points[pi];
					if (!TryProjectInsideRoi(pt.X, pt.Y, roi, axisU, axisV, out var u, out var v))
					{
						continue;
					}
					EdgePointSample sample = new EdgePointSample
					{
						CandidateIndex = ci,
						PointIndex = pi,
						Point = pt,
						U = u,
						V = v
					};
					samples.Add(sample);
					long key = MakePointKey(ci, pi);
					int uBin = CoordinateToBin(u, roi.HalfLen1, uBins);
					int vBin = CoordinateToBin(v, roi.HalfLen2, vBins);
					UpdateBestSidePoint(roi.HalfLen2 - v, key, uBin, topDist, topKey);
					UpdateBestSidePoint(roi.HalfLen2 + v, key, uBin, bottomDist, bottomKey);
					UpdateBestSidePoint(roi.HalfLen1 - u, key, vBin, rightDist, rightKey);
					UpdateBestSidePoint(roi.HalfLen1 + u, key, vBin, leftDist, leftKey);
				}
			}

			if (samples.Count == 0)
			{
				return empty;
			}
			HashSet<long> selectedKeys = new HashSet<long>();
			AddBestKeys(topKey, selectedKeys);
			AddBestKeys(bottomKey, selectedKeys);
			AddBestKeys(rightKey, selectedKeys);
			AddBestKeys(leftKey, selectedKeys);
			if (selectedKeys.Count == 0)
			{
				return empty;
			}

			List<FeatureSegment> segments = new List<FeatureSegment>();
			for (int ci = 0; ci < candidates.Count; ci++)
			{
				ContourCandidate candidate = candidates[ci];
				List<PointF> run = new List<PointF>();
				for (int pi = 0; pi < candidate.Points.Count; pi++)
				{
					if (selectedKeys.Contains(MakePointKey(ci, pi)))
					{
						run.Add(candidate.Points[pi]);
					}
					else
					{
						AppendFeatureSegment(run, segments, minDistance);
						run.Clear();
					}
				}
				AppendFeatureSegment(run, segments, minDistance);
			}
			if (segments.Count == 0)
			{
				return empty;
			}
			int maxSegmentCount = Math.Max(8, Math.Min(120, maxTotalPoints / 8));
			List<FeatureSegment> selected = segments
				.OrderByDescending(s => s.Score)
				.Take(maxSegmentCount)
				.ToList();
			return BuildPointListFromSegments(selected, minDistance, maxTotalPoints);
		}

		private static double[] CreateBestDistanceArray(int count)
		{
			double[] values = new double[count];
			for (int i = 0; i < values.Length; i++)
			{
				values[i] = double.PositiveInfinity;
			}
			return values;
		}

		private static long[] CreateBestKeyArray(int count)
		{
			long[] values = new long[count];
			for (int i = 0; i < values.Length; i++)
			{
				values[i] = -1L;
			}
			return values;
		}

		private static void UpdateBestSidePoint(double distanceFromSide, long key, int bin, double[] bestDistance, long[] bestKey)
		{
			if (bestDistance == null || bestKey == null || bin < 0 || bin >= bestDistance.Length)
			{
				return;
			}
			if (distanceFromSide < 0.0)
			{
				return;
			}
			if (distanceFromSide < bestDistance[bin])
			{
				bestDistance[bin] = distanceFromSide;
				bestKey[bin] = key;
			}
		}

		private static void AddBestKeys(long[] keys, HashSet<long> selectedKeys)
		{
			if (keys == null || selectedKeys == null)
			{
				return;
			}
			for (int i = 0; i < keys.Length; i++)
			{
				if (keys[i] >= 0)
				{
					selectedKeys.Add(keys[i]);
				}
			}
		}

		private static int CoordinateToBin(double value, double halfLen, int binCount)
		{
			if (binCount <= 1)
			{
				return 0;
			}
			double normalized = (value + halfLen) / Math.Max(1.0, halfLen * 2.0);
			int bin = (int)Math.Round(normalized * (binCount - 1));
			if (bin < 0)
			{
				return 0;
			}
			if (bin >= binCount)
			{
				return binCount - 1;
			}
			return bin;
		}

		private static long MakePointKey(int candidateIndex, int pointIndex)
		{
			return ((long)candidateIndex << 32) | (uint)pointIndex;
		}

		private static List<PointF> SelectOuterEnvelopePointSegments(List<ContourCandidate> candidates, RotRectF roi, PointF axisU, PointF axisV, double minDistance, int maxTotalPoints, int angleBinHint)
		{
			List<PointF> empty = new List<PointF>();
			if (candidates == null || candidates.Count == 0)
			{
				return empty;
			}
			int binCount = Math.Max(120, Math.Min(720, angleBinHint <= 0 ? 360 : angleBinHint));
			double[] envelope = Enumerable.Repeat(double.NegativeInfinity, binCount).ToArray();
			foreach (ContourCandidate candidate in candidates)
			{
				foreach (PointF pt in candidate.Points)
				{
					if (!TryProjectInsideRoi(pt.X, pt.Y, roi, axisU, axisV, out var u, out var v))
					{
						continue;
					}
					int bin = GetEnvelopeBin(u, v, binCount);
					double radius = GetNormalizedRadius(u, v, roi);
					if (radius > envelope[bin])
					{
						envelope[bin] = radius;
					}
				}
			}

			double[] smoothEnvelope = new double[binCount];
			for (int i = 0; i < binCount; i++)
			{
				double best = double.NegativeInfinity;
				for (int offset = -2; offset <= 2; offset++)
				{
					int idx = (i + offset + binCount) % binCount;
					if (envelope[idx] > best)
					{
						best = envelope[idx];
					}
				}
				smoothEnvelope[i] = double.IsNegativeInfinity(best) ? 0.0 : best;
			}

			List<FeatureSegment> segments = new List<FeatureSegment>();
			double tolerance = 0.055;
			double minRadius = 0.28;
			foreach (ContourCandidate candidate in candidates.OrderByDescending(c => c.Score))
			{
				List<PointF> run = new List<PointF>();
				foreach (PointF pt in candidate.Points)
				{
					bool keep = false;
					if (TryProjectInsideRoi(pt.X, pt.Y, roi, axisU, axisV, out var u, out var v))
					{
						int bin = GetEnvelopeBin(u, v, binCount);
						double radius = GetNormalizedRadius(u, v, roi);
						keep = radius >= minRadius && radius >= smoothEnvelope[bin] - tolerance;
					}
					if (keep)
					{
						run.Add(pt);
					}
					else
					{
						AppendFeatureSegment(run, segments, minDistance);
						run.Clear();
					}
				}
				AppendFeatureSegment(run, segments, minDistance);
			}

			if (segments.Count == 0)
			{
				return empty;
			}
			int maxSegmentCount = Math.Max(8, Math.Min(80, maxTotalPoints / 12));
			List<FeatureSegment> selected = segments
				.OrderByDescending(s => s.Score)
				.Take(maxSegmentCount)
				.ToList();
			return BuildPointListFromSegments(selected, minDistance, maxTotalPoints);
		}

		private static List<PointF> SelectLongestContourPoints(List<ContourCandidate> candidates, double minDistance, int maxTotalPoints)
		{
			if (candidates == null || candidates.Count == 0)
			{
				return new List<PointF>();
			}
			List<FeatureSegment> segments = candidates
				.OrderByDescending(c => c.Score)
				.Take(Math.Max(4, Math.Min(40, maxTotalPoints / 20)))
				.Select(c => new FeatureSegment
				{
					Points = c.Points,
					Length = c.Length
				})
				.ToList();
			return BuildPointListFromSegments(segments, minDistance, maxTotalPoints);
		}

		private static void AppendFeatureSegment(List<PointF> run, List<FeatureSegment> segments, double minDistance)
		{
			if (run == null || segments == null || run.Count < 2)
			{
				return;
			}
			double length = ContourLength(run);
			if (length < Math.Max(6.0, minDistance * 1.5))
			{
				return;
			}
			segments.Add(new FeatureSegment
			{
				Points = new List<PointF>(run),
				Length = length
			});
		}

		private static List<PointF> BuildPointListFromSegments(IList<FeatureSegment> segments, double minDistance, int maxTotalPoints)
		{
			List<PointF> result = new List<PointF>();
			if (segments == null || segments.Count == 0)
			{
				return result;
			}
			int maxPerSegment = Math.Max(4, maxTotalPoints / Math.Max(1, segments.Count));
			foreach (FeatureSegment segment in segments.OrderByDescending(s => s.Score))
			{
				List<PointF> sampled = ResampleOrderedContour(segment.Points, minDistance, maxPerSegment);
				if (sampled.Count < 2)
				{
					continue; 
				}
				if (result.Count > 0) 
				{
					result.Add(CreateContourSeparator());
				}
				result.AddRange(sampled);
				if (CountRealPoints(result) >= maxTotalPoints)
				{
					break;
				}
			}
			return LimitRealPointCount(result, maxTotalPoints);
		}

		private static List<PointF> BuildProportionalPointListFromSegments(IList<FeatureSegment> segments, double minDistance, int maxTotalPoints)
		{
			List<PointF> result = new List<PointF>();
			if (segments == null || segments.Count == 0)
			{
				return result;
			}
			List<FeatureSegment> ordered = segments
				.Where(s => s != null && s.Points != null && s.Points.Count >= 2)
				.OrderByDescending(s => s.Score)
				.Take(Math.Max(16, Math.Min(2000, maxTotalPoints / 4)))
				.ToList();
			if (ordered.Count == 0)
			{
				return result;
			}
			double totalLength = ordered.Sum(s => Math.Max(1.0, s.Length));
			foreach (FeatureSegment segment in ordered)
			{
				int maxForSegment = Math.Max(4, (int)Math.Round(maxTotalPoints * Math.Max(1.0, segment.Length) / totalLength));
				List<PointF> sampled = ResampleOrderedContour(segment.Points, minDistance, maxForSegment);
				if (sampled.Count < 2)
				{
					continue;
				}
				if (result.Count > 0)
				{
					result.Add(CreateContourSeparator());
				}
				result.AddRange(sampled);
				if (CountRealPoints(result) >= maxTotalPoints)
				{
					break;
				}
			}
			return LimitRealPointCount(result, maxTotalPoints);
		}

		private static List<PointF> LimitRealPointCount(List<PointF> points, int maxRealPoints)
		{
			if (points == null || CountRealPoints(points) <= maxRealPoints)
			{
				return points ?? new List<PointF>();
			}
			List<List<PointF>> segments = new List<List<PointF>>();
			List<PointF> current = new List<PointF>();
			foreach (PointF pt in points)
			{
				if (IsContourSeparator(pt))
				{
					if (current.Count > 0)
					{
						segments.Add(current);
						current = new List<PointF>();
					}
					continue;
				}
				current.Add(pt);
			}
			if (current.Count > 0)
			{
				segments.Add(current);
			}
			int total = segments.Sum(s => s.Count);
			if (total <= maxRealPoints)
			{
				return points;
			}
			List<PointF> result = new List<PointF>();
			foreach (List<PointF> segment in segments)
			{
				int keep = Math.Max(2, (int)Math.Round((double)segment.Count * maxRealPoints / total));
				List<PointF> sampled = ResampleOrderedContour(segment, 1.0, keep);
				if (sampled.Count < 2)
				{
					continue;
				}
				if (result.Count > 0)
				{
					result.Add(CreateContourSeparator());
				}
				result.AddRange(sampled);
			}
			return result;
		}

		private static double ContourLength(IList<PointF> points)
		{
			if (points == null || points.Count < 2)
			{
				return 0.0;
			}
			double length = 0.0;
			for (int i = 1; i < points.Count; i++)
			{
				length += Math.Sqrt(DistanceSquared(points[i - 1], points[i]));
			}
			return length;
		}

		private static ContourCandidate BuildContourCandidate(HTuple rows, HTuple cols, RotRectF roi, PointF axisU, PointF axisV)
		{
			ContourCandidate candidate = new ContourCandidate();
			int len = Math.Min(rows.Length, cols.Length);
			double minU = double.PositiveInfinity;
			double maxU = double.NegativeInfinity;
			double minV = double.PositiveInfinity;
			double maxV = double.NegativeInfinity;
			PointF? last = null;
			for (int p = 0; p < len; p++)
			{
				double x = cols[p].D;
				double y = rows[p].D;
				if (!TryProjectInsideRoi(x, y, roi, axisU, axisV, out var u, out var v))
				{
					continue;
				}
				PointF pt = new PointF((float)x, (float)y);
				candidate.Points.Add(pt);
				minU = Math.Min(minU, u);
				maxU = Math.Max(maxU, u);
				minV = Math.Min(minV, v);
				maxV = Math.Max(maxV, v);
				if (last.HasValue)
				{
					candidate.Length += Math.Sqrt(DistanceSquared(last.Value, pt));
				}
				last = pt;
			}
			if (candidate.Points.Count > 0 && !double.IsInfinity(minU) && !double.IsInfinity(minV))
			{
				candidate.Coverage = Math.Max(0.0, maxU - minU) * Math.Max(0.0, maxV - minV);
			}
			return candidate;
		}

		private static IEnumerable<ContourCandidate> SelectEnvelopeContourSegments(List<ContourCandidate> candidates, RotRectF roi, PointF axisU, PointF axisV)
		{
			const int binCount = 180;
			double[] envelope = Enumerable.Repeat(double.NegativeInfinity, binCount).ToArray();
			foreach (ContourCandidate candidate in candidates)
			{
				foreach (PointF pt in candidate.Points)
				{
					if (!TryProjectInsideRoi(pt.X, pt.Y, roi, axisU, axisV, out var u, out var v))
					{
						continue;
					}
					int bin = GetEnvelopeBin(u, v, binCount);
					double radius = GetNormalizedRadius(u, v, roi);
					if (radius > envelope[bin])
					{
						envelope[bin] = radius;
					}
				}
			}

			for (int i = 0; i < envelope.Length; i++)
			{
				if (double.IsNegativeInfinity(envelope[i]))
				{
					envelope[i] = 0.0;
				}
			}

			List<Tuple<ContourCandidate, double>> scored = new List<Tuple<ContourCandidate, double>>();
			foreach (ContourCandidate candidate in candidates)
			{
				int outer = 0;
				int total = 0;
				foreach (PointF pt in candidate.Points)
				{
					if (!TryProjectInsideRoi(pt.X, pt.Y, roi, axisU, axisV, out var u, out var v))
					{
						continue;
					}
					int bin = GetEnvelopeBin(u, v, binCount);
					double radius = GetNormalizedRadius(u, v, roi);
					total++;
					if (radius >= envelope[bin] - 0.06)
					{
						outer++;
					}
				}
				if (total == 0)
				{
					continue;
				}
				double ratio = (double)outer / (double)total;
				if (ratio >= 0.35)
				{
					scored.Add(Tuple.Create(candidate, candidate.Score * (0.6 + ratio)));
				}
			}
			return scored.OrderByDescending(x => x.Item2).Select(x => x.Item1);
		}

		private static int GetEnvelopeBin(double u, double v, int binCount)
		{
			double angle = Math.Atan2(v, u);
			int bin = (int)Math.Floor((angle + Math.PI) / (Math.PI * 2.0) * binCount);
			if (bin < 0)
			{
				return 0;
			}
			if (bin >= binCount)
			{
				return binCount - 1;
			}
			return bin;
		}

		private static double GetNormalizedRadius(double u, double v, RotRectF roi)
		{
			double ru = u / Math.Max(1.0, roi.HalfLen1);
			double rv = v / Math.Max(1.0, roi.HalfLen2);
			return Math.Sqrt(ru * ru + rv * rv);
		}

		private static List<PointF> ResampleOrderedContour(IList<PointF> points, double minDistance, int maxPoints)
		{
			List<PointF> result = new List<PointF>();
			if (points == null || points.Count == 0)
			{
				return result;
			}
			double minDist2 = minDistance * minDistance;
			result.Add(points[0]);
			for (int i = 1; i < points.Count; i++)
			{
				PointF pt = points[i];
				if (DistanceSquared(pt, result[result.Count - 1]) >= minDist2)
				{
					result.Add(pt);
				}
			}
			if (result.Count > maxPoints)
			{
				List<PointF> limited = new List<PointF>();
				double step = (double)(result.Count - 1) / (double)(maxPoints - 1);
				for (int i = 0; i < maxPoints; i++)
				{
					int idx = Math.Min(result.Count - 1, (int)Math.Round(i * step));
					limited.Add(result[idx]);
				}
				result = limited;
			}
			return result;
		}

		private static List<PointF> SelectSparseOuterPoints(HObject edges, RotRectF roi, int bins, double minDistance)
		{
			List<PointF> candidates = new List<PointF>();
			if (edges == null || roi.IsEmpty)
			{
				return candidates;
			}
			int binCount = Math.Max(24, Math.Min(1440, bins <= 0 ? 360 : bins));
			double minDist = Math.Max(0.0, minDistance);
			PointF axisU = roi.GetAxisU();
			PointF axisV = roi.GetAxisV();
			PointF[] bestPoints = new PointF[binCount];
			double[] bestRadius = Enumerable.Repeat(double.NegativeInfinity, binCount).ToArray();
			bool[] hasPoint = new bool[binCount];
			HOperatorSet.CountObj(edges, out var countTuple);
			int count = countTuple.Length > 0 ? countTuple[0].I : 0;
			for (int i = 1; i <= count; i++)
			{
				HObject contour = null;
				try
				{
					HOperatorSet.SelectObj(edges, out contour, i);
					HOperatorSet.GetContourXld(contour, out var rows, out var cols);
					int len = Math.Min(rows.Length, cols.Length);
					for (int p = 0; p < len; p++)
					{
						double x = cols[p].D;
						double y = rows[p].D;
						if (!TryProjectInsideRoi(x, y, roi, axisU, axisV, out var u, out var v))
						{
							continue;
						}
						double angle = Math.Atan2(v, u);
						int bin = (int)Math.Floor((angle + Math.PI) / (Math.PI * 2.0) * binCount);
						if (bin < 0)
						{
							bin = 0;
						}
						else if (bin >= binCount)
						{
							bin = binCount - 1;
						}
						double ru = Math.Abs(u) / Math.Max(1.0, roi.HalfLen1);
						double rv = Math.Abs(v) / Math.Max(1.0, roi.HalfLen2);
						double radius = Math.Sqrt(ru * ru + rv * rv);
						if (radius > bestRadius[bin])
						{
							bestRadius[bin] = radius;
							bestPoints[bin] = new PointF((float)x, (float)y);
							hasPoint[bin] = true;
						}
					}
				}
				finally
				{
					contour?.Dispose();
				}
			}
			List<Tuple<PointF, double>> ordered = new List<Tuple<PointF, double>>();
			for (int i = 0; i < binCount; i++)
			{
				if (!hasPoint[i] || bestRadius[i] < 0.15)
				{
					continue;
				}
				PointF pt = bestPoints[i];
				double a = Math.Atan2(pt.Y - roi.Center.Y, pt.X - roi.Center.X);
				ordered.Add(Tuple.Create(pt, a));
			}
			foreach (Tuple<PointF, double> item in ordered.OrderBy(x => x.Item2))
			{
				PointF pt = item.Item1;
				bool tooClose = false;
				for (int i = 0; i < candidates.Count; i++)
				{
					if (DistanceSquared(pt, candidates[i]) < minDist * minDist)
					{
						tooClose = true;
						break;
					}
				}
				if (!tooClose)
				{
					candidates.Add(pt);
				}
			}
			return candidates;
		}

		private static List<PointF> NormalizeTemplateFeaturePoints(IList<PointF> featurePoints, RotRectF roi, double minDistance)
		{
			List<PointF> result = new List<PointF>();
			if (featurePoints == null || roi.IsEmpty)
			{
				return result;
			}
			double minDist = Math.Max(0.0, minDistance);
			PointF axisU = roi.GetAxisU();
			PointF axisV = roi.GetAxisV();
			PointF? lastInSegment = null;
			foreach (PointF pt in featurePoints)
			{
				if (IsContourSeparator(pt))
				{
					if (result.Count > 0 && !IsContourSeparator(result[result.Count - 1]))
					{
						result.Add(CreateContourSeparator());
					}
					lastInSegment = null;
					continue;
				}
				if (!TryProjectInsideRoi(pt.X, pt.Y, roi, axisU, axisV, out var _, out var _))
				{
					continue;
				}
				if (minDist <= 0.0 || !lastInSegment.HasValue || DistanceSquared(pt, lastInSegment.Value) >= minDist * minDist)
				{
					result.Add(pt);
					lastInSegment = pt;
				}
			}
			while (result.Count > 0 && IsContourSeparator(result[result.Count - 1]))
			{
				result.RemoveAt(result.Count - 1);
			}
			return result;
		}

		private static void GenContourFromPoints(List<PointF> points, out HObject contour)
		{
			if (points == null || points.Count == 0)
			{
				throw new ArgumentException("Feature point list is empty.", "points");
			}
			contour = null;
			List<PointF> segment = new List<PointF>();
			for (int i = 0; i <= points.Count; i++)
			{
				bool flush = i == points.Count || IsContourSeparator(points[i]);
				if (!flush)
				{
					segment.Add(points[i]);
					continue;
				}
				AppendContourSegment(segment, ref contour);
				segment.Clear();
			}
			if (contour == null)
			{
				throw new ArgumentException("Feature point list does not contain a valid contour.", "points");
			}
		}

		private static void AppendContourSegment(List<PointF> segment, ref HObject contour)
		{
			if (segment == null || segment.Count < 2)
			{
				return;
			}
			HTuple rows = new HTuple();
			HTuple cols = new HTuple();
			for (int i = 0; i < segment.Count; i++)
			{
				rows = rows.TupleConcat(segment[i].Y);
				cols = cols.TupleConcat(segment[i].X);
			}
			HObject one = null;
			HObject concat = null;
			try
			{
				HOperatorSet.GenContourPolygonXld(out one, rows, cols);
				if (contour == null)
				{
					contour = one;
					one = null;
					return;
				}
				HOperatorSet.ConcatObj(contour, one, out concat);
				contour.Dispose();
				contour = concat;
				concat = null;
			}
			finally
			{
				one?.Dispose();
				concat?.Dispose();
			}
		}

		private static bool TryProjectInsideRoi(double x, double y, RotRectF roi, PointF axisU, PointF axisV, out double u, out double v)
		{
			double dx = x - roi.Center.X;
			double dy = y - roi.Center.Y;
			u = dx * axisU.X + dy * axisU.Y;
			v = dx * axisV.X + dy * axisV.Y;
			return Math.Abs(u) <= roi.HalfLen1 && Math.Abs(v) <= roi.HalfLen2;
		}

		private static PointF CreateContourSeparator()
		{
			return new PointF(float.NaN, float.NaN);
		}

		private static bool IsContourSeparator(PointF p)
		{
			return float.IsNaN(p.X) || float.IsNaN(p.Y);
		}

		private static int CountRealPoints(IEnumerable<PointF> points)
		{
			return points == null ? 0 : points.Count(p => !IsContourSeparator(p));
		}

		private static double DistanceSquared(PointF a, PointF b)
		{
			double dx = a.X - b.X;
			double dy = a.Y - b.Y;
			return dx * dx + dy * dy;
		}

		private static List<PointF> TransformPoints(IList<PointF> points, HTuple hom)
		{
			List<PointF> result = new List<PointF>();
			if (points == null || hom == null)
			{
				return result;
			}
			for (int i = 0; i < points.Count; i++)
			{
				PointF pt = points[i];
				if (IsContourSeparator(pt))
				{
					result.Add(CreateContourSeparator());
					continue;
				}
				HOperatorSet.AffineTransPoint2d(hom, pt.Y, pt.X, out var row, out var col);
				result.Add(new PointF((float)col.D, (float)row.D));
			}
			return result;
		}
		private static List<TemplateEraseStroke> CloneEraseStrokes(IEnumerable<TemplateEraseStroke> strokes)
		{
			return strokes == null
				? new List<TemplateEraseStroke>()
				: strokes.Select(x => x?.DeepClone() ?? new TemplateEraseStroke()).ToList();
		}
		private static void SetXldModelOriginToTemplateRoiCenter(HTuple modelID, IList<PointF> featurePoints, RotRectF templateRoi)
		{
			if (modelID == null || modelID.Length <= 0 || featurePoints == null || templateRoi.IsEmpty)
			{
				return;
			}
			bool hasPoint = false;
			double minRow = double.MaxValue;
			double maxRow = double.MinValue;
			double minCol = double.MaxValue;
			double maxCol = double.MinValue;
			foreach (PointF pt in featurePoints)
			{
				if (IsContourSeparator(pt))
				{
					continue;
				}
				hasPoint = true;
				minRow = Math.Min(minRow, pt.Y);
				maxRow = Math.Max(maxRow, pt.Y);
				minCol = Math.Min(minCol, pt.X);
				maxCol = Math.Max(maxCol, pt.X);
			}
			if (!hasPoint)
			{
				return;
			}
			double defaultOriginRow = (minRow + maxRow) * 0.5;
			double defaultOriginCol = (minCol + maxCol) * 0.5;
			HOperatorSet.SetShapeModelOrigin(modelID, templateRoi.Center.Y - defaultOriginRow, templateRoi.Center.X - defaultOriginCol);
		}

		private static void FindTemplateMatch(HObject image, HTuple modelID, EdgeInspectJob job, out HTuple row, out HTuple column, out HTuple angle, out HTuple score)
		{
			HObject searchRegion = null;
			HObject reducedImage = null;
			try
			{
				HObject imageForMatch = image;
				if (job != null && job.UseTemplateSearchRoi && IsUsableSearchRoi(job.TemplateSearchRoi))
				{
					GenRectangle2(job.TemplateSearchRoi, out searchRegion);
					HOperatorSet.ReduceDomain(image, searchRegion, out reducedImage);
					imageForMatch = reducedImage;
				}
				HOperatorSet.FindShapeModel(imageForMatch, modelID, job.Match.AngleStart, job.Match.AngleExtent, job.Match.MinScore, 1, 0.3, "least_squares", 0, 0.75, out row, out column, out angle, out score);
			}
			finally
			{
				reducedImage?.Dispose();
				searchRegion?.Dispose();
			}
		}

		private static void ResolveReferenceMatchPose(HObject image, HTuple modelID, EdgeInspectJob job, out double refRow, out double refCol, out double refAngle)
		{
			FindTemplateMatch(image, modelID, job, out var row, out var column, out var angle, out var score);
			if (score == null || score.Length < 1)
			{
				throw new InvalidOperationException("模板模型无法在示教图中回匹配，请检查模板ROI、搜索区域和匹配参数。");
			}
			refRow = row[0].D;
			refCol = column[0].D;
			refAngle = angle[0].D;
		}

		private static void BuildReferenceToMatchTransform(EdgeInspectJob job, HTuple row, HTuple column, HTuple angle, out HTuple homMat2D)
		{
			double refRow = job.TeachData.RefRow;
			double refCol = job.TeachData.RefCol;
			double refAngle = job.TeachData.RefAngle;
			if (double.IsNaN(refRow) || double.IsInfinity(refRow) || double.IsNaN(refCol) || double.IsInfinity(refCol))
			{
				refRow = job.TemplateRoi.Center.Y;
				refCol = job.TemplateRoi.Center.X;
			}
			if (double.IsNaN(refAngle) || double.IsInfinity(refAngle))
			{
				refAngle = 0.0;
			}
			HOperatorSet.VectorAngleToRigid(refRow, refCol, refAngle, row[0], column[0], angle[0], out homMat2D);
		}

		private static bool IsUsableSearchRoi(RotRectF roi)
		{
			return !roi.IsEmpty && !float.IsNaN(roi.Cx) && !float.IsNaN(roi.Cy) && !float.IsNaN(roi.Phi) && !float.IsNaN(roi.HalfLen1) && !float.IsNaN(roi.HalfLen2) && !float.IsInfinity(roi.Cx) && !float.IsInfinity(roi.Cy) && !float.IsInfinity(roi.Phi) && !float.IsInfinity(roi.HalfLen1) && !float.IsInfinity(roi.HalfLen2);
		}

		public TemplateMatchQuickTestResult TestTemplateMatch(Bitmap curBmp, EdgeInspectJob job)
		{
			if (curBmp == null)
			{
				throw new ArgumentNullException("curBmp");
			}
			if (job == null)
			{
				throw new ArgumentNullException("job");
			}
			job = job.DeepClone();
			job.Normalize();
			if (job.TeachData == null || !job.TeachData.HasTemplate || job.TeachData.ModelBytes == null || job.TeachData.ModelBytes.Length == 0)
			{
				throw new InvalidOperationException("请先创建模板模型。");
			}
			HImage hImage = null;
			GCHandle grayHandle = default(GCHandle);
			HTuple modelID = null;
			try
			{
				hImage = BitmapToGrayHImage(curBmp, out var _, out var _, out var _, out grayHandle);
				modelID = ImportShapeModelFromBytes(job.TeachData.ModelBytes);
				SetXldModelOriginToTemplateRoiCenter(modelID, job.TeachData.FeaturePoints, job.TemplateRoi);
				FindTemplateMatch(hImage, modelID, job, out var row, out var column, out var angle, out var score);
				if (score.Length < 1)
				{
					return new TemplateMatchQuickTestResult
					{
						Found = false,
						Message = LocalizedText.Message("未找到匹配目标。", job.Language)
					};
				}
				BuildReferenceToMatchTransform(job, row, column, angle, out var homMat2D);
				return new TemplateMatchQuickTestResult
				{
					Found = true,
					Score = score[0].D,
					Row = row[0].D,
					Col = column[0].D,
					Angle = angle[0].D,
					TemplateRoiCur = TransformRotRect(job.TemplateRoi, homMat2D),
					// Search ROI stays in the current image coordinate system.
					// The template and detection ROIs are the ones that follow the match transform.
					TemplateSearchRoiCur = job.Match.Enabled && job.UseTemplateSearchRoi ? job.TemplateSearchRoi : default(RotRectF),
					MatchContourPoints = TransformPoints(job.TeachData.FeaturePoints, homMat2D),
					Message = LocalizedText.Message($"匹配成功，分数={score[0].D:F3}", job.Language)
				};
			}
			finally 
			{
				if (modelID != null && modelID.Length > 0)
				{
					try
					{
						HOperatorSet.ClearShapeModel(modelID);
					}
					catch
					{
					}
				}
				hImage?.Dispose();
				if (grayHandle.IsAllocated)
				{
					grayHandle.Free();
				}
			}
		}
		public EdgeInspectResult Inspect(Bitmap curBmp, EdgeInspectJob job)
		{
			if (curBmp == null)
			{
				throw new ArgumentNullException("curBmp");
			}
			if (job == null)
			{
				throw new ArgumentNullException("job");
			}
			job = job.DeepClone();
			job.Normalize();
			bool requiresAnyBaseRoi = job.RequiresAnyBaseRoi;
			bool hasAnySelfJudgeDetect = job.HasAnySelfJudgeDetect;
			if (!job.IsReadyForInspect)
			{
				throw new InvalidOperationException(requiresAnyBaseRoi ? "需要：至少一个检测ROI；其中使用基准线判定的检测ROI还需要至少一个基准ROI。" : "需要：至少一个检测ROI。");
			}
			if (job.Match.Enabled && (job.TeachData == null || !job.TeachData.HasTemplate || job.TeachData.ModelBytes == null || job.TeachData.ModelBytes.Length == 0))
			{
				throw new InvalidOperationException("Job 中未包含有效模板数据。");
			}
			bool flag = job.UseExternalBurrTolerance && job.ExternalBurrTolerance >= 0.0 && job.PixelResolutionX > 0.0 && job.PixelResolutionY > 0.0;
			EdgeInspectResult edgeInspectResult = new EdgeInspectResult
			{
				Language = job.Language,
				DetectMode = job.DetectMode,
				TemplateMatchEnabled = job.Match.Enabled,
				TemplateMatchOk = !job.Match.Enabled,
				TemplateRoiCur = job.TemplateRoi,
				TemplateSearchRoiCur = job.Match.Enabled && job.UseTemplateSearchRoi ? job.TemplateSearchRoi : default(RotRectF),
				UseReferenceLine = (requiresAnyBaseRoi && !hasAnySelfJudgeDetect),
				HasReferenceLineItems = requiresAnyBaseRoi,
				HasSelfFitLineItems = hasAnySelfJudgeDetect,
				UseExternalBurrTolerance = flag,
				ExternalBurrTolerance = job.ExternalBurrTolerance,
				ExternalDentTolerance = job.ExternalDentTolerance,
				ExternalOverEdgeTolerance = job.ExternalOverEdgeTolerance,
				ExternalCopperLeakTolerance = job.ExternalCopperLeakTolerance,
				PixelResolutionX = job.PixelResolutionX,
				PixelResolutionY = job.PixelResolutionY
			};
			HImage hImage = null;
			GCHandle grayHandle = default(GCHandle);
			HTuple hTuple = null;
			try
			{
				hImage = BitmapToGrayHImage(curBmp, out var _, out var w, out var h, out grayHandle);
				HTuple homMat2D = null;
				bool flag2 = false;
				if (job.Match.Enabled)
				{
					hTuple = ImportShapeModelFromBytes(job.TeachData.ModelBytes);
					SetXldModelOriginToTemplateRoiCenter(hTuple, job.TeachData.FeaturePoints, job.TemplateRoi);
					FindTemplateMatch(hImage, hTuple, job, out var row, out var column, out var angle, out var score);
					if (score.Length < 1)
					{
						edgeInspectResult.TemplateMatchOk = false;
						edgeInspectResult.Success = false;
						edgeInspectResult.NgReasons |= NgReason.TemplateMatchFailed;
						edgeInspectResult.FailedItems.Add("模板匹配失败");
						LocalizedText.ApplyToResult(edgeInspectResult, job.Language);
						edgeInspectResult.Message = BuildHostMessage(edgeInspectResult);
						return edgeInspectResult;
					}
					edgeInspectResult.TemplateMatchOk = true;
					edgeInspectResult.TemplateMatchScore = score[0].D;
					edgeInspectResult.TemplateMatchRow = row[0].D;
					edgeInspectResult.TemplateMatchCol = column[0].D;
					edgeInspectResult.TemplateMatchAngle = angle[0].D;
					edgeInspectResult.TemplateMatchCenter = new PointF((float)column[0].D, (float)row[0].D);
					BuildReferenceToMatchTransform(job, row, column, angle, out homMat2D);
					flag2 = true;
					edgeInspectResult.TemplateRoiCur = TransformRotRect(job.TemplateRoi, homMat2D);
					// Search ROI remains fixed in image coordinates after a successful match.
					edgeInspectResult.TemplateSearchRoiCur = job.Match.Enabled && job.UseTemplateSearchRoi ? job.TemplateSearchRoi : default(RotRectF);
					edgeInspectResult.TemplateMatchContourPoints.AddRange(TransformPoints(job.TeachData.FeaturePoints, homMat2D));
				}
				Dictionary<int, EdgeLineFit> dictionary = new Dictionary<int, EdgeLineFit>();
				Dictionary<int, EdgeLineFit> dictionary2 = new Dictionary<int, EdgeLineFit>();
				Dictionary<int, EdgeCircleFit> dictionary3 = new Dictionary<int, EdgeCircleFit>();
				for (int i = 0; i < job.BaseRois.Count; i++)
				{
					BaseRoiItem baseRoiItem = job.BaseRois[i] ?? new BaseRoiItem
					{
						Name = $"基准{i + 1}"
					};
					RotRectF rotRectF = (flag2 ? TransformRotRect(baseRoiItem.Roi, homMat2D) : baseRoiItem.Roi);
					CaliperParameters caliperParameters = (baseRoiItem.Caliper ?? job.ResolveBaseCaliper(i) ?? EdgeInspectJob.CreateDefaultBaseCaliper()).DeepClone();
					BaseRoiInspectResult baseRoiInspectResult = new BaseRoiInspectResult
					{
						Index = i,
						Name = (string.IsNullOrWhiteSpace(baseRoiItem.Name) ? $"基准{i + 1}" : baseRoiItem.Name),
						RoiCur = rotRectF,
						CaliperUsed = caliperParameters.DeepClone()
					};
					if (rotRectF.IsEmpty)
					{
						baseRoiInspectResult.Success = false;
						baseRoiInspectResult.Message = "基准ROI为空";
					}
					else
					{
						baseRoiInspectResult.Line = FitLineInRotRoi(hImage, w, h, rotRectF, caliperParameters, useNormalRatioFit: false);
						baseRoiInspectResult.Success = baseRoiInspectResult.Line != null && baseRoiInspectResult.Line.Success;
						baseRoiInspectResult.Message = (baseRoiInspectResult.Success ? "OK" : AppendFailureDetail("基准线拟合失败", baseRoiInspectResult.Line));
						if (baseRoiInspectResult.Success)
						{
							dictionary[i] = baseRoiInspectResult.Line;
						}
					}
					edgeInspectResult.BaseResults.Add(baseRoiInspectResult);
				}
				for (int j = 0; j < job.CircleBaseRois.Count; j++)
				{
					CircleBaseRoiItem circleBaseRoiItem = job.CircleBaseRois[j] ?? new CircleBaseRoiItem
					{
						Name = $"圆基准{j + 1}"
					};
					CircleRoiF circleRoiF = (flag2 ? TransformCircleRoi(circleBaseRoiItem.Circle1, homMat2D) : circleBaseRoiItem.Circle1);
					CircleRoiF circleRoiF2 = (flag2 ? TransformCircleRoi(circleBaseRoiItem.Circle2, homMat2D) : circleBaseRoiItem.Circle2);
					CaliperParameters caliperParameters2 = (circleBaseRoiItem.Caliper ?? job.CircleCaliper ?? EdgeInspectJob.CreateDefaultCircleCaliper()).DeepClone();
					CircleBaseRoiInspectResult circleBaseRoiInspectResult = new CircleBaseRoiInspectResult
					{
						Index = j,
						Name = (string.IsNullOrWhiteSpace(circleBaseRoiItem.Name) ? $"圆基准{j + 1}" : circleBaseRoiItem.Name),
						Circle1RoiCur = circleRoiF,  
						Circle2RoiCur = circleRoiF2,
						CaliperUsed = caliperParameters2.DeepClone()
					};
					if (circleRoiF.IsEmpty || circleRoiF2.IsEmpty)
					{
						circleBaseRoiInspectResult.Success = false;
						circleBaseRoiInspectResult.Message = "圆基准ROI为空";
					}
					else
					{
						circleBaseRoiInspectResult.Circle1 = FitCircleInRoi(hImage, w, h, circleRoiF, caliperParameters2);
						circleBaseRoiInspectResult.Circle2 = FitCircleInRoi(hImage, w, h, circleRoiF2, caliperParameters2);
						if (circleBaseRoiInspectResult.Circle1.Success && circleBaseRoiInspectResult.Circle2.Success)
						{
							circleBaseRoiInspectResult.Line = LineFromCircleCenters(circleBaseRoiInspectResult.Circle1.Center, circleBaseRoiInspectResult.Circle2.Center, w, h);
							circleBaseRoiInspectResult.Success = circleBaseRoiInspectResult.Line.Success;
							circleBaseRoiInspectResult.Message = (circleBaseRoiInspectResult.Success ? "OK" : "圆心连线无效");
							if (circleBaseRoiInspectResult.Success)
							{
								dictionary2[j] = circleBaseRoiInspectResult.Line;
							}
						}
						else
						{
							circleBaseRoiInspectResult.Success = false;
							circleBaseRoiInspectResult.Message = "圆拟合失败：" + (circleBaseRoiInspectResult.Circle1.Success ? "" : ("圆1 " + circleBaseRoiInspectResult.Circle1.Message + " ")) + (circleBaseRoiInspectResult.Circle2.Success ? "" : ("圆2 " + circleBaseRoiInspectResult.Circle2.Message));
						}
					}
					edgeInspectResult.CircleBaseResults.Add(circleBaseRoiInspectResult);
				}
				for (int k = 0; k < job.CirclePointRois.Count; k++)
				{
					CirclePointRoiItem circlePointRoiItem = job.CirclePointRois[k] ?? new CirclePointRoiItem
					{
						Name = $"圆点基准{k + 1}"
					};
					CircleRoiF circleRoiF3 = (flag2 ? TransformCircleRoi(circlePointRoiItem.Circle, homMat2D) : circlePointRoiItem.Circle);
					CaliperParameters caliperParameters3 = (circlePointRoiItem.Caliper ?? job.CircleCaliper ?? EdgeInspectJob.CreateDefaultCircleCaliper()).DeepClone();
					CirclePointRoiInspectResult circlePointRoiInspectResult = new CirclePointRoiInspectResult
					{
						Index = k,
						Name = (string.IsNullOrWhiteSpace(circlePointRoiItem.Name) ? $"圆点基准{k + 1}" : circlePointRoiItem.Name),
						CircleRoiCur = circleRoiF3,
						CaliperUsed = caliperParameters3.DeepClone()
					};
					if (circleRoiF3.IsEmpty)
					{
						circlePointRoiInspectResult.Success = false;
						circlePointRoiInspectResult.Message = "圆点基准ROI为空";
					}
					else
					{
						circlePointRoiInspectResult.Circle = FitCircleInRoi(hImage, w, h, circleRoiF3, caliperParameters3);
						circlePointRoiInspectResult.Success = circlePointRoiInspectResult.Circle != null && circlePointRoiInspectResult.Circle.Success;
						circlePointRoiInspectResult.Message = (circlePointRoiInspectResult.Success ? "OK" : AppendFailureDetail("圆点基准拟合失败", circlePointRoiInspectResult.Circle));
						if (circlePointRoiInspectResult.Success)
						{
							dictionary3[k] = circlePointRoiInspectResult.Circle;
						}
					}
					edgeInspectResult.CirclePointResults.Add(circlePointRoiInspectResult);
				}
				if (edgeInspectResult.BaseResults.Count > 0)
				{
					edgeInspectResult.BaseRoiCur = edgeInspectResult.BaseResults[0].RoiCur;
					edgeInspectResult.BaseLine = edgeInspectResult.BaseResults[0].Line ?? new EdgeLineFit();
				}
				int num = 0;
				List<double> list = new List<double>();
				List<double> list2 = new List<double>();
				for (int l = 0; l < job.DetectItems.Count; l++)
				{
					DetectRoiItem detectRoiItem = job.DetectItems[l] ?? new DetectRoiItem
					{
						Name = $"检测{l + 1}"
					};
					if (!detectRoiItem.Enabled)
					{
						continue;
					}
					num++;
					RotRectF rotRectF2 = (flag2 ? TransformRotRect(detectRoiItem.Roi, homMat2D) : detectRoiItem.Roi);
					CaliperParameters caliperParameters4 = (detectRoiItem.Caliper ?? job.ResolveDetectCaliper(l) ?? EdgeInspectJob.CreateDefaultDetectCaliper()).DeepClone();
					int num2 = job.ResolveBaseRoiIndex(detectRoiItem.BaseRoiId, detectRoiItem.BaseRoiIndex);
					int num3 = job.ResolveCircleBaseRoiIndex(detectRoiItem.CircleBaseRoiId, detectRoiItem.CircleBaseRoiIndex);
					int num4 = job.ResolveCirclePointRoiIndex(detectRoiItem.CirclePointRoiId, detectRoiItem.CirclePointRoiIndex);
					DetectRoiInspectResult detectRoiInspectResult = new DetectRoiInspectResult
					{
						Index = l,
						Language = job.Language,
						Name = (string.IsNullOrWhiteSpace(detectRoiItem.Name) ? $"检测{l + 1}" : detectRoiItem.Name),
						Enabled = true,
						RoiCur = rotRectF2,
						UseReferenceLine = detectRoiItem.UseReferenceLine,
						ReferenceBaseKind = detectRoiItem.ReferenceBaseKind,
						BaseRoiIndex = num2,
						CircleBaseRoiIndex = num3,
						CirclePointRoiIndex = num4,
						AngleReference = detectRoiItem.AngleReference,
						NominalDistancePx = detectRoiItem.NominalDistancePx,
						ReferencePointCur = rotRectF2.Center,
						BurrTolerancePx = detectRoiItem.BurrTolerancePx,
						DentTolerancePx = detectRoiItem.DentTolerancePx,
						DetectMode = job.DetectMode,
						ConsecutiveAbnormalPointCount = job.ConsecutiveAbnormalPointCount,
						CaliperUsed = caliperParameters4.DeepClone(),
						UseExternalBurrTolerance = flag,
						ExternalBurrTolerance = job.ExternalBurrTolerance,
						ExternalDentTolerance = job.ExternalDentTolerance,
						ExternalOverEdgeTolerance = job.ExternalOverEdgeTolerance,
						ExternalCopperLeakTolerance = job.ExternalCopperLeakTolerance,
						PixelResolutionX = job.PixelResolutionX,
						PixelResolutionY = job.PixelResolutionY
					};
					if (rotRectF2.IsEmpty)
					{
						AddDetectFailure(edgeInspectResult, detectRoiInspectResult, "检测ROI为空");
						continue;
					}
					detectRoiInspectResult.FittedLine = FitLineInRotRoi(hImage, w, h, rotRectF2, caliperParameters4, useNormalRatioFit: true);
					if (detectRoiInspectResult.FittedLine == null || !detectRoiInspectResult.FittedLine.Success || detectRoiInspectResult.FittedLine.MeasurePoints.Count == 0)
					{
						AddDetectFailure(edgeInspectResult, detectRoiInspectResult, AppendFailureDetail("检测ROI拟合失败（检查：ROI/卡尺参数/极性）", detectRoiInspectResult.FittedLine));
						continue;
					}
					detectRoiInspectResult.FittedAngleRad = detectRoiInspectResult.FittedLine.AngleRad;
					EdgeLineFit edgeLineFit = null;
					PointF? pointF = null;
					if (detectRoiItem.ReferenceBaseKind == ReferenceBaseKind.CirclePoint)
					{
						detectRoiInspectResult.UseReferenceLine = false;
						if (!dictionary3.TryGetValue(num4, out var value) || value == null || !value.Success)
						{
							AddDetectFailure(edgeInspectResult, detectRoiInspectResult, $"关联圆点基准无效（圆点基准索引={num4 + 1}）");
							continue;
						}
						detectRoiInspectResult.CirclePointRoiIndex = num4;
						detectRoiInspectResult.ReferencePointCur = value.Center;
						detectRoiInspectResult.JudgeLine = CopyLine(detectRoiInspectResult.FittedLine);
						edgeLineFit = detectRoiInspectResult.FittedLine;
						pointF = value.Center;
					}
					else if (detectRoiItem.UseReferenceLine)
					{
						EdgeLineFit value2 = null;
						if (detectRoiItem.ReferenceBaseKind == ReferenceBaseKind.CirclePair)
						{
							dictionary2.TryGetValue(num3, out value2);
							detectRoiInspectResult.CircleBaseRoiIndex = num3;
						}
						else
						{
							dictionary.TryGetValue(num2, out value2);
						}
						if (value2 == null || !value2.Success)
						{
							string text = ((detectRoiItem.ReferenceBaseKind == ReferenceBaseKind.CirclePair) ? $"圆基准索引={detectRoiInspectResult.CircleBaseRoiIndex + 1}" : $"基准索引={num2 + 1}");
							AddDetectFailure(edgeInspectResult, detectRoiInspectResult, "关联基准线无效（" + text + "）");
							continue;
						}
						edgeLineFit = value2;
						detectRoiInspectResult.JudgeLine = CopyLine(value2);
					}
					else
					{
						detectRoiInspectResult.JudgeLine = CopyLine(detectRoiInspectResult.FittedLine);
					}
					ComputeAngleReference(detectRoiInspectResult, dictionary, dictionary2);
					EvaluateDetectItem(detectRoiInspectResult, rotRectF2, job.DetectMode, useReferenceLine: detectRoiInspectResult.UseReferenceLine);
					if (edgeLineFit != null && edgeLineFit.Success)
					{
						if (pointF.HasValue)
						{
							EvaluateOverallDistance(detectRoiInspectResult, edgeLineFit, pointF.Value);
						}
						else
						{
							EvaluateOverallDistanceFromFittedLine(detectRoiInspectResult, edgeLineFit);
						}
					}
					if (!detectRoiInspectResult.Success)
					{
						edgeInspectResult.FailedItems.Add(detectRoiInspectResult.Name + ": " + detectRoiInspectResult.Message);
					}
					edgeInspectResult.NgReasons |= detectRoiInspectResult.NgReasons;
					edgeInspectResult.BurrCount += detectRoiInspectResult.BurrCount;
					edgeInspectResult.DentCount += detectRoiInspectResult.DentCount;
					if (detectRoiInspectResult.IsOverEdge)
					{
						edgeInspectResult.OverEdgeCount++;
					}
					if (detectRoiInspectResult.IsCopperLeak)
					{
						edgeInspectResult.CopperLeakCount++;
					}
					edgeInspectResult.Points.AddRange(detectRoiInspectResult.Points);
					list.AddRange(detectRoiInspectResult.Points.Select((EdgePointResult p) => p.SignedDistancePx));
					list2.AddRange(detectRoiInspectResult.Points.Select((EdgePointResult p) => p.DeltaPx));
					edgeInspectResult.MaxPositiveDeltaPx = Math.Max(edgeInspectResult.MaxPositiveDeltaPx, detectRoiInspectResult.MaxPositiveDeltaPx);
					edgeInspectResult.MaxPositiveDeltaValue = Math.Max(edgeInspectResult.MaxPositiveDeltaValue, detectRoiInspectResult.MaxPositiveDeltaValue);
					edgeInspectResult.DetectResults.Add(detectRoiInspectResult);
				}
				if (edgeInspectResult.DetectResults.Count > 0)
				{
					DetectRoiInspectResult detectRoiInspectResult2 = edgeInspectResult.DetectResults[0];
					edgeInspectResult.DetectRoiCur = detectRoiInspectResult2.RoiCur;
					edgeInspectResult.Nominal = detectRoiInspectResult2.NominalDistancePx;
					edgeInspectResult.BurrTolerance = detectRoiInspectResult2.BurrTolerancePx;
					edgeInspectResult.DentTolerance = detectRoiInspectResult2.DentTolerancePx;
				}
				if (list.Count > 0)
				{
					edgeInspectResult.SignedMin = list.Min();
					edgeInspectResult.SignedMax = list.Max();
					edgeInspectResult.SignedMean = list.Average();
				}
				if (list2.Count > 0)
				{
					edgeInspectResult.DeltaMin = list2.Min();
					edgeInspectResult.DeltaMax = list2.Max();
					edgeInspectResult.DeltaMean = list2.Average();
				}
				bool flag3 = num > 0 && edgeInspectResult.DetectResults.All((DetectRoiInspectResult x) => x.Success);
				edgeInspectResult.Success = edgeInspectResult.TemplateMatchOk && flag3;
				if (!edgeInspectResult.Success && edgeInspectResult.NgReasons == NgReason.None)
				{
					edgeInspectResult.NgReasons = NgReason.DetectRoiFailed;
				}
				LocalizedText.ApplyToResult(edgeInspectResult, job.Language);
				edgeInspectResult.Message = BuildHostMessage(edgeInspectResult);
				return edgeInspectResult;
			}
			finally
			{
				if (hTuple != null && hTuple.Length > 0)
				{
					try
					{
						HOperatorSet.ClearShapeModel(hTuple);
					}
					catch
					{
					}
				}
				hImage?.Dispose();
				if (grayHandle.IsAllocated)
				{
					grayHandle.Free();
				}
			}
		}
		public void Dispose()
		{

		}
		private static string BuildHostMessage(EdgeInspectResult result)
		{
			if (result == null)
			{
				return "总体=NG,毛刺公差=0.0000,凹陷公差=0.0000";
			}
			bool useValue = result.UseExternalBurrTolerance;
			string unit = useValue ? "mm" : "px";
			double burrTolerance = useValue ? result.ExternalBurrTolerance : result.BurrTolerance;
			double dentTolerance = useValue ? result.ExternalDentTolerance : result.DentTolerance;
			List<string> parts = new List<string>
			{
				$"总体={(result.Success ? "OK" : "NG")},毛刺公差={burrTolerance:F4}{unit},凹陷公差={dentTolerance:F4}{unit}"
			};
			foreach (DetectRoiInspectResult dr in result.DetectResults.Where((DetectRoiInspectResult x) => x != null))
			{
				double maxBurr;
				double maxDent;
				GetMaxBurrDent(dr, useValue, out maxBurr, out maxDent);
				string roiName = string.IsNullOrWhiteSpace(dr.Name) ? $"检测{dr.Index + 1}" : dr.Name;
				parts.Add($"{roiName}={(dr.Success ? "OK" : "NG")},最大毛刺={maxBurr:F4}{unit},最大凹陷={maxDent:F4}{unit}");
			}
			return string.Join(";", parts);
		}
		private static void GetMaxBurrDent(DetectRoiInspectResult dr, bool useValue, out double maxBurr, out double maxDent)
		{
			maxBurr = 0.0;
			maxDent = 0.0;
			if (dr == null)
			{
				return;
			}
			foreach (EdgePointResult point in dr.Points.Where((EdgePointResult x) => x != null))
			{
				AddDeltaToMax(useValue ? point.DeltaValue : point.DeltaPx, ref maxBurr, ref maxDent);
			}
			if (dr.HasOverallDistance)
			{
				AddDeltaToMax(useValue ? dr.OverallDeltaValue : dr.OverallDeltaPx, ref maxBurr, ref maxDent);
			}
		}
		private static void AddDeltaToMax(double delta, ref double maxBurr, ref double maxDent)
		{
			if (delta < 0.0)
			{
				maxBurr = Math.Max(maxBurr, Math.Abs(delta));
			}
			else if (delta > 0.0)
			{
				maxDent = Math.Max(maxDent, delta);
			}
		}
		private static void AddDetectFailure(EdgeInspectResult res, DetectRoiInspectResult dr, string message)
		{
			if (dr != null)
			{
				dr.Success = false;
				dr.Message = message ?? "失败";
				dr.NgReasons |= ResolveFailureReason(dr.Message);
				res.NgReasons |= dr.NgReasons;
				res.DetectResults.Add(dr);
				string text = (string.IsNullOrWhiteSpace(dr.Name) ? $"检测{dr.Index + 1}" : dr.Name);
				res.FailedItems.Add(text + ": " + dr.Message);
			}
		}
		private static NgReason ResolveFailureReason(string message)
		{
			if (string.IsNullOrWhiteSpace(message))
			{
				return NgReason.DetectRoiFailed;
			}
			if (message.Contains("基准"))
			{
				return NgReason.BaseRoiFailed;
			}
			if (message.Contains("检测ROI") || message.Contains("找边") || message.Contains("拟合"))
			{
				return NgReason.DetectRoiFailed;
			}
			return NgReason.DetectRoiFailed;
		}
		private static string AppendFailureDetail(string message, EdgeLineFit fit)
		{
			if (fit == null || string.IsNullOrWhiteSpace(fit.Message))
			{
				return message;
			}
			return message + "：" + fit.Message;
		}
		private static string AppendFailureDetail(string message, EdgeCircleFit fit)
		{
			if (fit == null || string.IsNullOrWhiteSpace(fit.Message))
			{
				return message;
			}
			return message + "：" + fit.Message;
		}
		private static void EvaluateDetectItem(DetectRoiInspectResult dr, RotRectF detRoi, DefectDetectMode mode, bool useReferenceLine)
		{
			if (dr == null)
			{
				return;
			}
			EdgeLineFit distanceLine = useReferenceLine ? dr.JudgeLine : dr.FittedLine;
			if (distanceLine == null || !distanceLine.Success)
			{
				dr.Success = false;
				dr.Message = "判定线无效";
				return;
			}
			if (dr.FittedLine == null || dr.FittedLine.MeasurePoints.Count == 0)
			{
				dr.Success = false;
				dr.Message = "未取得检测点";
				return;
			}
			bool useReferenceJudgeLine = useReferenceLine;
			SignedLine signedLine = SignedLine.FromLine(distanceLine.P1, distanceLine.P2);
			double num7 = ResolvePositiveDirectionScale(detRoi, signedLine);
			PointF fittedLineCenter = new PointF((dr.FittedLine.P1.X + dr.FittedLine.P2.X) * 0.5f, (dr.FittedLine.P1.Y + dr.FittedLine.P2.Y) * 0.5f);
			double nominalDistancePx = useReferenceJudgeLine ? ResolveSignedNominalDistancePx(detRoi, signedLine, dr.NominalDistancePx, num7, fittedLineCenter) : 0.0;
			bool flag = mode == DefectDetectMode.Both || mode == DefectDetectMode.BurrOnly;
			bool flag2 = mode == DefectDetectMode.Both || mode == DefectDetectMode.DentOnly;
			bool flag3 = dr.UseExternalBurrTolerance && dr.ExternalBurrTolerance >= 0.0 && dr.PixelResolutionX > 0.0 && dr.PixelResolutionY > 0.0;
			double num = double.PositiveInfinity;
			double num2 = double.NegativeInfinity;
			double num3 = 0.0;
			double num4 = double.PositiveInfinity;
			double num5 = double.NegativeInfinity;
			double num6 = 0.0;
			int consecutiveAbnormal = 0;
			bool qualifiedAbnormal = false;
			bool qualifiedBurr = false;
			bool qualifiedDent = false;
			bool currentRunHasBurr = false;
			bool currentRunHasDent = false;
			int requiredConsecutive = Math.Max(1, dr.ConsecutiveAbnormalPointCount);
			double physicalDistancePerPixel = GetPhysicalDistancePerPixel(signedLine, dr.PixelResolutionX, dr.PixelResolutionY);
			for (int i = 0; i < dr.FittedLine.MeasurePoints.Count; i++)
			{
				PointF pointF = dr.FittedLine.MeasurePoints[i];
				double num8 = signedLine.SignedDistance(pointF);
				double num9 = num8 * num7;
				double num10 = num9 - nominalDistancePx;
				double signedDistanceValue = num9 * physicalDistancePerPixel;
				double num11 = num10 * physicalDistancePerPixel;
				// Negative deviation is burr; positive deviation is dent.
				bool flag4 = flag3 ? (flag && num11 < 0.0 - dr.ExternalBurrTolerance) : (flag && num10 < 0.0 - dr.BurrTolerancePx);
				bool flag5 = flag3 ? (flag2 && num11 > dr.ExternalDentTolerance) : (flag2 && num10 > dr.DentTolerancePx);
				if (flag4)
				{
					dr.BurrCount++;
				}
				if (flag5)
				{
					dr.DentCount++;
				}
				if (flag4 || flag5)
				{
					consecutiveAbnormal++;
					currentRunHasBurr |= flag4;
					currentRunHasDent |= flag5;
					if (consecutiveAbnormal >= requiredConsecutive)
					{
						qualifiedAbnormal = true;
						qualifiedBurr |= currentRunHasBurr;
						qualifiedDent |= currentRunHasDent;
					}
				}
				else
				{
					consecutiveAbnormal = 0;
					currentRunHasBurr = false;
					currentRunHasDent = false;
				}
				dr.MaxConsecutiveAbnormalCount = Math.Max(dr.MaxConsecutiveAbnormalCount, consecutiveAbnormal);
				NgReason ngReason = NgReason.None;
				if (flag4)
				{
					ngReason |= NgReason.Burr;
				}
				if (flag5)
				{
					ngReason |= NgReason.Dent;
				}
				num = Math.Min(num, num9);
				num2 = Math.Max(num2, num9);
				num3 += num9;
				num4 = Math.Min(num4, num10);
				num5 = Math.Max(num5, num10);
				num6 += num10;
				if (num10 > dr.MaxPositiveDeltaPx)
				{
					dr.MaxPositiveDeltaPx = num10;
				}
				if (num11 > dr.MaxPositiveDeltaValue)
				{
					dr.MaxPositiveDeltaValue = num11;
				}
				dr.Points.Add(new EdgePointResult
				{
					DetectIndex = dr.Index,
					DetectName = dr.Name,
					Index = i,
					Point = pointF,
					SignedDistanceRawPx = num8,
					SignedDistancePx = num9,
					DeltaPx = num10,
					SignedDistanceValue = signedDistanceValue,
					DeltaValue = num11,
					IsBurr = flag4,
					IsDent = flag5,
					NgReasons = ngReason
				});
			}
			dr.SignedMin = num;
			dr.SignedMax = num2;
			dr.SignedMean = num3 / (double)dr.FittedLine.MeasurePoints.Count;
			dr.DeltaMin = num4;
			dr.DeltaMax = num5;
			dr.DeltaMean = num6 / (double)dr.FittedLine.MeasurePoints.Count;
			if (qualifiedAbnormal)
			{
				if (qualifiedBurr)
				{
					dr.NgReasons |= NgReason.Burr;
				}
				if (qualifiedDent)
				{
					dr.NgReasons |= NgReason.Dent;
				}
			}
			dr.Success = !qualifiedAbnormal;
			object obj;
			switch (mode)
			{
			default:
				obj = "毛刺+凹陷";
				break;
			case DefectDetectMode.DentOnly:
				obj = "凹陷";
				break;
			case DefectDetectMode.BurrOnly:
				obj = "毛刺";
				break;
			}
			string text = (string)obj;
			string text2 = useReferenceJudgeLine ? "基准线局部判定" : "自拟合线局部判定";
			string text3 = (double.IsNaN(dr.AngleDeltaDeg) ? "角差=N/A" : $"角差={dr.AngleDeltaDeg:F3}°");
			string text4 = (flag3 ? $"外部允差(mm)：毛刺={dr.ExternalBurrTolerance:F4} 凹陷={dr.ExternalDentTolerance:F4} | 解析度X={dr.PixelResolutionX:F6}mm/px | 解析度Y={dr.PixelResolutionY:F6}mm/px | 最大凹陷={dr.MaxPositiveDeltaValue:F4}mm" : $"本地毛刺允差={dr.BurrTolerancePx:F2}px | 本地凹陷允差={dr.DentTolerancePx:F2}px | 最大凹陷={dr.MaxPositiveDeltaPx:F2}px");
			dr.Message = (dr.Success ? $"OK | {text2} | 模式={text} | {text4} | 连续异常点={dr.MaxConsecutiveAbnormalCount}/{requiredConsecutive} | {text3} | 局部Δ(min/max/mean)={dr.DeltaMin:F2}/{dr.DeltaMax:F2}/{dr.DeltaMean:F2}px" : $"NG | {text2} | 模式={text} | {text4} | 连续异常点={dr.MaxConsecutiveAbnormalCount}/{requiredConsecutive} | 毛刺={dr.BurrCount} 凹陷={dr.DentCount} | {text3} | 局部Δ(min/max/mean)={dr.DeltaMin:F2}/{dr.DeltaMax:F2}/{dr.DeltaMean:F2}px");
		}

		private static void EvaluateOverallDistance(DetectRoiInspectResult dr, EdgeLineFit referenceLine, PointF measurePoint)
		{
			EvaluateOverallDistance(dr, referenceLine, measurePoint, null);
		}

		private static void EvaluateOverallDistanceFromFittedLine(DetectRoiInspectResult dr, EdgeLineFit referenceLine)
		{
			if (dr == null || dr.FittedLine == null || !dr.FittedLine.Success || referenceLine == null || !referenceLine.Success)
			{
				return;
			}
			if (TryGetRobustOverallMeasurePoint(dr, referenceLine, out var measurePoint))
			{
				EvaluateOverallDistance(dr, referenceLine, measurePoint);
				return;
			}
			PointF fallbackPoint = new PointF((dr.FittedLine.P1.X + dr.FittedLine.P2.X) * 0.5f, (dr.FittedLine.P1.Y + dr.FittedLine.P2.Y) * 0.5f);
			EvaluateOverallDistance(dr, referenceLine, fallbackPoint);
		}
		private static void EvaluateOverallDistanceFromMeasurePoints(DetectRoiInspectResult dr, EdgeLineFit referenceLine)
		{
			if (TryGetRobustOverallMeasurePoint(dr, referenceLine, out var measurePoint))
			{
				EvaluateOverallDistance(dr, referenceLine, measurePoint);
			}
		}
		private static bool TryGetRobustOverallMeasurePoint(DetectRoiInspectResult dr, EdgeLineFit referenceLine, out PointF measurePoint)
		{
			measurePoint = default(PointF);
			if (dr == null || dr.FittedLine == null || dr.FittedLine.MeasurePoints == null || dr.FittedLine.MeasurePoints.Count == 0 || referenceLine == null || !referenceLine.Success)
			{
				return false;
			}
			SignedLine signedLine = SignedLine.FromLine(referenceLine.P1, referenceLine.P2);
			double directionScale = ResolvePositiveDirectionScale(dr.RoiCur, signedLine);
			List<Tuple<double, PointF>> samples = new List<Tuple<double, PointF>>(dr.FittedLine.MeasurePoints.Count);
			foreach (PointF point in dr.FittedLine.MeasurePoints)
			{
				double signedDistance = signedLine.SignedDistance(point) * directionScale;
				if (!double.IsNaN(signedDistance) && !double.IsInfinity(signedDistance))
				{
					samples.Add(Tuple.Create(signedDistance, point));
				}
			}
			if (samples.Count == 0)
			{
				return false;
			}
			samples.Sort((Tuple<double, PointF> a, Tuple<double, PointF> b) => a.Item1.CompareTo(b.Item1));
			int middle = samples.Count / 2;
			if (samples.Count % 2 == 0 && samples.Count > 1)
			{
				PointF lower = samples[middle - 1].Item2;
				PointF upper = samples[middle].Item2;
				measurePoint = new PointF((lower.X + upper.X) * 0.5f, (lower.Y + upper.Y) * 0.5f);
			}
			else
			{
				measurePoint = samples[middle].Item2;
			}
			return true;
		}
		private static void EvaluateOverallDistance(DetectRoiInspectResult dr, EdgeLineFit referenceLine, PointF measurePoint, double? measuredDistancePx)
		{
			if (dr != null && referenceLine != null && referenceLine.Success)
			{
				SignedLine signedLine = SignedLine.FromLine(referenceLine.P1, referenceLine.P2);
				PointF pointF = ProjectPointToLine(measurePoint, referenceLine.P1, referenceLine.P2);
				double value = signedLine.SignedDistance(measurePoint);
				// Resolve sign from the detection ROI arrow, independent of line endpoint order.
				double directionScale = ResolvePositiveDirectionScale(dr.RoiCur, signedLine);
				double signedNominalDistancePx = ResolveSignedNominalDistancePx(dr.RoiCur, signedLine, dr.NominalDistancePx, directionScale, measurePoint);
				double signedDistancePx = value * directionScale;
				double num = (measuredDistancePx.HasValue ? Math.Abs(measuredDistancePx.Value) : Math.Abs(signedDistancePx));
				double physicalDistancePerPixel = GetPhysicalDistancePerPixel(signedLine, dr.PixelResolutionX, dr.PixelResolutionY);
				double num2 = num * physicalDistancePerPixel;
				double num5 = GetNominalDistanceValue(dr);
				double signedNominalDistanceValue = Math.Sign(signedNominalDistancePx) * Math.Abs(num5);
				double num4 = signedDistancePx * physicalDistancePerPixel - signedNominalDistanceValue;
				double num3 = signedDistancePx - signedNominalDistancePx;
				bool flag = dr.UseExternalBurrTolerance && dr.ExternalBurrTolerance >= 0.0 && dr.PixelResolutionX > 0.0 && dr.PixelResolutionY > 0.0;
				// Negative deviation is over-edge; positive deviation is copper leak.
				bool flag2 = (flag ? (num4 < 0.0 - dr.ExternalOverEdgeTolerance) : (num3 < 0.0 - dr.BurrTolerancePx));
				bool flag3 = (flag ? (num4 > dr.ExternalCopperLeakTolerance) : (num3 > dr.DentTolerancePx));
				dr.OverallMeasurePoint = measurePoint;
				dr.OverallFootPoint = pointF;
				dr.OverallReferenceLine = CopyLine(referenceLine);
				dr.ReferenceFootPoint = pointF;
				dr.HasOverallDistance = true;
				dr.HasPointToLineDistance = true;
				dr.OverallDistancePx = num;
				dr.OverallDistanceValue = num2;
				dr.OverallDeltaPx = num3;
				dr.OverallDeltaValue = num4;
				dr.IsOverEdge = flag2;
				dr.IsCopperLeak = flag3;
				if (flag2)
				{
					dr.NgReasons |= NgReason.OverEdge;
				}
				if (flag3)
				{
					dr.NgReasons |= NgReason.CopperLeak;
				}
				dr.MaxPositiveDeltaPx = Math.Max(dr.MaxPositiveDeltaPx, num3);
				dr.MaxPositiveDeltaValue = Math.Max(dr.MaxPositiveDeltaValue, num4);
				if (flag2 || flag3)
				{
					dr.Success = false;
				}
				string text = (flag ? $"外部允差(mm)：超边={dr.ExternalOverEdgeTolerance:F4} | 漏铜={dr.ExternalCopperLeakTolerance:F4}" : $"毛刺允差={dr.BurrTolerancePx:F2}px | 凹陷允差={dr.DentTolerancePx:F2}px");
				string text2 = $" | 整体距离={num2:F4}mm 标准={num5:F4}mm Δ={num4:+0.0000;-0.0000;0.0000}mm | 像素距离={num:F2}px Δ={num3:+0.00;-0.00;0.00}px | {text}";
				if (flag2)
				{
					dr.Message = dr.Message + " | 超边NG" + text2;
				}
				else if (flag3)
				{
					dr.Message = dr.Message + " | 漏铜NG" + text2;
				}
				else
				{
					dr.Message = dr.Message + " | 整体距离OK" + text2;
				}
			}
		}

		private static string GetDetectModeText(DefectDetectMode mode)
		{
			switch (mode)
			{
			case DefectDetectMode.BurrOnly:
				return "毛刺";
			case DefectDetectMode.DentOnly:
				return "凹陷";
			case DefectDetectMode.PointToCutEdgeDistance:
				return "点到切割边距离";
			default:
				return "毛刺+凹陷";
			}
		}

		private static void ComputeAngleReference(DetectRoiInspectResult dr, Dictionary<int, EdgeLineFit> baseLineMap, Dictionary<int, EdgeLineFit> circleBaseLineMap)
		{
			if (dr == null)
			{
				return;
			}
			double? num = null;
			string angleReferenceText;
			switch (dr.AngleReference)
			{
			case DetectAngleReferenceMode.Horizontal:
				num = 0.0;
				angleReferenceText = "水平";
				break;
			case DetectAngleReferenceMode.Vertical:
				num = Math.PI / 2.0;
				angleReferenceText = "竖直";
				break;
			default:
			{
				EdgeLineFit value2;
				if (dr.ReferenceBaseKind == ReferenceBaseKind.CirclePair)
				{
					if (circleBaseLineMap != null && circleBaseLineMap.TryGetValue(dr.CircleBaseRoiIndex, out var value) && value != null && value.Success)
					{
						num = value.AngleRad;
						angleReferenceText = $"平行于圆基准{dr.CircleBaseRoiIndex + 1}";
					}
					else
					{
						angleReferenceText = $"平行于圆基准{dr.CircleBaseRoiIndex + 1}(不可用)";
					}
				}
				else if (baseLineMap != null && baseLineMap.TryGetValue(dr.BaseRoiIndex, out value2) && value2 != null && value2.Success)
				{
					num = value2.AngleRad;
					angleReferenceText = $"平行于基准{dr.BaseRoiIndex + 1}";
				}
				else
				{
					angleReferenceText = $"平行于基准{dr.BaseRoiIndex + 1}(不可用)";
				}
				break;
			}
			}
			dr.AngleReferenceText = angleReferenceText;
			if (!num.HasValue)
			{
				dr.AngleDeltaRad = double.NaN;
				dr.AngleDeltaDeg = double.NaN;
			}
			else
			{
				dr.RefAngleRad = num.Value;
				dr.AngleDeltaRad = NormalizeLineAngleDelta(dr.FittedAngleRad - dr.RefAngleRad);
				dr.AngleDeltaDeg = dr.AngleDeltaRad * 180.0 / Math.PI;
			}
		}

		private static double NormalizeLineAngleDelta(double delta)
		{
			while (delta > Math.PI)
			{
				delta -= Math.PI * 2.0;
			}
			while (delta < -Math.PI)
			{
				delta += Math.PI * 2.0;
			}
			if (delta > Math.PI / 2.0)
			{
				delta -= Math.PI;
			}
			if (delta < -Math.PI / 2.0)
			{
				delta += Math.PI;
			}
			return delta;
		}

		private static double ResolvePositiveDirectionScale(RotRectF rr, SignedLine line)
		{
			double num = 0.0 - Math.Sin(rr.AngleRad);
			double num2 = Math.Cos(rr.AngleRad);
			double num3 = num * line.NCol + num2 * line.NRow;
			return (num3 >= 0.0) ? 1.0 : (-1.0);
		}

		private static double ResolveSignedNominalDistancePx(RotRectF roi, SignedLine line, double nominalDistancePx, double directionScale, PointF fallbackPoint)
		{
			if (line == null || Math.Abs(nominalDistancePx) < 1E-09)
			{
				return 0.0;
			}
			double roiCenterSide = line.SignedDistance(roi.Center) * directionScale;
			if (Math.Abs(roiCenterSide) < 1E-06)
			{
				roiCenterSide = line.SignedDistance(fallbackPoint) * directionScale;
			}
			if (Math.Abs(roiCenterSide) < 1E-06)
			{
				return Math.Abs(nominalDistancePx);
			}
			return Math.Sign(roiCenterSide) * Math.Abs(nominalDistancePx);
		}

		private static double GetPhysicalDistancePerPixel(SignedLine line, double pixelResolutionX, double pixelResolutionY)
		{
			if (line == null)
			{
				return 1.0;
			}
			if (pixelResolutionX <= 0.0 && pixelResolutionY > 0.0)
			{
				pixelResolutionX = pixelResolutionY;
			}
			if (pixelResolutionY <= 0.0 && pixelResolutionX > 0.0)
			{
				pixelResolutionY = pixelResolutionX;
			}
			if (pixelResolutionX <= 0.0)
			{
				pixelResolutionX = 1.0;
			}
			if (pixelResolutionY <= 0.0)
			{
				pixelResolutionY = 1.0;
			}
			double num = line.NCol * pixelResolutionX;
			double num2 = line.NRow * pixelResolutionY;
			double num3 = Math.Sqrt(num * num + num2 * num2);
			return (num3 > 0.0) ? num3 : ((pixelResolutionX + pixelResolutionY) * 0.5);
		}

		private static double GetNominalDistanceValue(DetectRoiInspectResult dr)
		{
			if (dr == null)
			{
				return 0.0;
			}
			double pixelResolutionX = dr.PixelResolutionX;
			double pixelResolutionY = dr.PixelResolutionY;
			if (pixelResolutionX <= 0.0 && pixelResolutionY > 0.0)
			{
				pixelResolutionX = pixelResolutionY;
			}
			if (pixelResolutionY <= 0.0 && pixelResolutionX > 0.0)
			{
				pixelResolutionY = pixelResolutionX;
			}
			if (pixelResolutionX <= 0.0)
			{
				pixelResolutionX = 1.0;
			}
			if (pixelResolutionY <= 0.0)
			{
				pixelResolutionY = 1.0;
			}
			return dr.NominalDistancePx * ((pixelResolutionX + pixelResolutionY) * 0.5);
		}

		private static PointF ProjectPointToLine(PointF p, PointF a, PointF b)
		{
			double num = b.X - a.X;
			double num2 = b.Y - a.Y;
			double num3 = num * num + num2 * num2;
			if (num3 <= 1E-09)
			{
				return a;
			}
			double num4 = ((double)(p.X - a.X) * num + (double)(p.Y - a.Y) * num2) / num3;
			return new PointF((float)((double)a.X + num4 * num), (float)((double)a.Y + num4 * num2));
		}

		private static byte[] ExportShapeModelToBytes(HTuple modelId)
		{
			if (modelId == null || modelId.Length == 0)
			{
				return null;
			}
			HTuple serializedItemHandle = null;
			try
			{
				HOperatorSet.SerializeShapeModel(modelId, out serializedItemHandle);
				HOperatorSet.GetSerializedItemPtr(serializedItemHandle, out var pointer, out var size);
				int i = size[0].I;
				if (i <= 0)
				{
					return null;
				}
				byte[] array = new byte[i];
				Marshal.Copy((IntPtr)pointer[0].L, array, 0, i);
				return array;
			}
			finally
			{
				if (serializedItemHandle != null && serializedItemHandle.Length > 0)
				{
					try
					{
						HOperatorSet.ClearSerializedItem(serializedItemHandle);
					}
					catch
					{
					}
				}
			}
		}

		private static HTuple ImportShapeModelFromBytes(byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0)
			{
				throw new InvalidOperationException("模板字节数据为空。");
			}
			GCHandle gCHandle = default(GCHandle);
			HTuple serializedItemHandle = null;
			try
			{
				gCHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
				HOperatorSet.CreateSerializedItemPtr(gCHandle.AddrOfPinnedObject(), bytes.Length, "true", out serializedItemHandle);
				HOperatorSet.DeserializeShapeModel(serializedItemHandle, out var modelID);
				return modelID;
			}
			finally
			{
				if (serializedItemHandle != null && serializedItemHandle.Length > 0)
				{
					try
					{
						HOperatorSet.ClearSerializedItem(serializedItemHandle);
					}
					catch
					{
					}
				}
				if (gCHandle.IsAllocated)
				{
					gCHandle.Free();
				}
			}
		}
		private static void GenRectangle2(RotRectF rr, out HObject rect2)
		{
			HOperatorSet.GenRectangle2(out rect2, rr.Center.Y, rr.Center.X, rr.AngleRad, rr.HalfLen1, rr.HalfLen2);
		}

		private static RotRectF TransformRotRect(RotRectF rr, HTuple hom)
		{
			if (rr.IsEmpty)
			{
				return rr;
			}
			HOperatorSet.AffineTransPoint2d(hom, rr.Center.Y, rr.Center.X, out var qx, out var qy);
			double num = (double)rr.Center.Y + Math.Sin(rr.AngleRad);
			double num2 = (double)rr.Center.X + Math.Cos(rr.AngleRad);
			HOperatorSet.AffineTransPoint2d(hom, num, num2, out var qx2, out var qy2);
			double num3 = Math.Atan2(qx2.D - qx.D, qy2.D - qy.D);
			return new RotRectF(new PointF((float)qy.D, (float)qx.D), RotRectF.NormalizeAngle((float)num3), rr.HalfLen1, rr.HalfLen2);
		}
		private static CircleRoiF TransformCircleRoi(CircleRoiF circle, HTuple hom)
		{
			if (circle.IsEmpty)
			{
				return circle;
			}
			HOperatorSet.AffineTransPoint2d(hom, circle.Center.Y, circle.Center.X, out var qx, out var qy);
			return new CircleRoiF(new PointF((float)qy.D, (float)qx.D), circle.Radius);
		}

		private static void ResolveRotRectToLine(RotRectF rr, out PtD p1, out PtD p2)
		{
			double num = Math.Cos(rr.AngleRad) * (double)rr.HalfLen1;
			double num2 = Math.Sin(rr.AngleRad) * (double)rr.HalfLen1;
			p1 = new PtD((double)rr.Center.X - num, (double)rr.Center.Y - num2);
			p2 = new PtD((double)rr.Center.X + num, (double)rr.Center.Y + num2);
		}

		private static EdgeLineFit FitLineInRotRoi(HImage gray, int width, int height, RotRectF rr, CaliperParameters cp, bool useNormalRatioFit)
		{
			EdgeLineFit edgeLineFit = new EdgeLineFit
			{
				Success = false
			};
			HObject rect = null;
			HObject imageReduced = null;
			HObject contours = null;
			HTuple metrologyHandle = new HTuple();
			try
			{
				CaliperParameters caliperParameters = cp?.DeepClone() ?? new CaliperParameters();
				NormalizeCaliper(caliperParameters);
				GenRectangle2(rr, out rect);
				HOperatorSet.ReduceDomain(gray, rect, out imageReduced);
				HOperatorSet.CreateMetrologyModel(out metrologyHandle);
				HOperatorSet.SetMetrologyModelImageSize(metrologyHandle, width, height);
				ResolveRotRectToLine(rr, out var p, out var p2);
				HOperatorSet.AddMetrologyObjectLineMeasure(metrologyHandle, p.Y, p.X, p2.Y, p2.X, Math.Max(1.0, caliperParameters.MeasureLength), Math.Max(1.0, caliperParameters.MeasureWidth), caliperParameters.Sigma, caliperParameters.Threshold, new HTuple(), new HTuple(), out var index);
				SetMetrologyParams(metrologyHandle, index, caliperParameters);
				HOperatorSet.ApplyMetrologyModel(imageReduced, metrologyHandle);
				HOperatorSet.GetMetrologyObjectMeasures(out contours, metrologyHandle, index, "all", out var row, out var column);
				if (row == null || row.Length < 2)
				{
					edgeLineFit.Message = "卡尺测量点不足";
					return edgeLineFit;
				}
				for (int i = 0; i < row.Length; i++)
				{     
					edgeLineFit.MeasurePoints.Add(new PointF((float)column[i].D, (float)row[i].D));
				}
				double r;
				double c;
				double r2;
				double c2;
				double angle;
				string error;
				bool flag = useNormalRatioFit
					? TryFitLineByNormalRatio(edgeLineFit.MeasurePoints, caliperParameters.LineFitNormalRatio, rr.AngleRad, width, height, out r, out c, out r2, out c2, out angle, out error)
					: TryFitLineStable(row, column, width, height, out r, out c, out r2, out c2, out angle, out error);
				if (flag)
				{
					edgeLineFit.P1 = new PointF((float)c, (float)r);
					edgeLineFit.P2 = new PointF((float)c2, (float)r2);
					edgeLineFit.AngleRad = angle;
					edgeLineFit.Success = true;
				}
				else
				{
					edgeLineFit.Message = (string.IsNullOrWhiteSpace(error) ? "直线拟合失败" : error);
				}
				return edgeLineFit;
			}
			catch (Exception ex)
			{
				edgeLineFit.Message = ex.GetType().Name + ": " + ex.Message;
				return edgeLineFit;
			}
			finally
			{
				contours?.Dispose();
				if (metrologyHandle != null && metrologyHandle.Length > 0)
				{
					try
					{
						HOperatorSet.ClearMetrologyModel(metrologyHandle);
					}
					catch
					{
					}
				}
				imageReduced?.Dispose();
				rect?.Dispose();
			}
		}

		private static EdgeCircleFit FitCircleInRoi(HImage gray, int width, int height, CircleRoiF roi, CaliperParameters cp)
		{
			EdgeCircleFit edgeCircleFit = new EdgeCircleFit
			{
				Success = false
			};
			try
			{
				CaliperParameters caliperParameters = cp?.DeepClone() ?? EdgeInspectJob.CreateDefaultCircleCaliper();
				NormalizeCaliper(caliperParameters);
				int num = Math.Max(8, caliperParameters.NumMeasures);
				double outward = Math.Max(0.0, caliperParameters.SearchOutward);
				double inwardLength = Math.Max(1.0, caliperParameters.MeasureLength);
				for (int i = 0; i < num; i++)
				{
					double angle = Math.PI * 2.0 * (double)i / (double)num;
					if (TryMeasureRadialCircleEdge(gray, width, height, roi, caliperParameters, angle, outward, inwardLength, out var row, out var col))
					{
						edgeCircleFit.MeasurePoints.Add(new PointF((float)col, (float)row));
					}
				}
				if (edgeCircleFit.MeasurePoints.Count < 3)
				{
					edgeCircleFit.Message = "圆测量点不足（请检查圆ROI、圆外扩、阈值/极性）";
					return edgeCircleFit;
				}
				HTuple rows = new HTuple(((IEnumerable<PointF>)edgeCircleFit.MeasurePoints).Select((Func<PointF, double>)((PointF p) => p.Y)).ToArray());
				HTuple cols = new HTuple(((IEnumerable<PointF>)edgeCircleFit.MeasurePoints).Select((Func<PointF, double>)((PointF p) => p.X)).ToArray());
				if (TryFitCircleStable(rows, cols, out var row2, out var col2, out var radius, out var error))
				{
					edgeCircleFit.Center = new PointF((float)col2, (float)row2);
					edgeCircleFit.Radius = radius;
					edgeCircleFit.Success = true;
				}
				else
				{
					edgeCircleFit.Message = (string.IsNullOrWhiteSpace(error) ? "圆拟合失败" : error);
				}
				return edgeCircleFit;
			}
			catch (Exception ex)
			{
				edgeCircleFit.Message = ex.GetType().Name + ": " + ex.Message;
				return edgeCircleFit;
			}
			finally
			{
			}
		}

		private static bool TryMeasureRadialCircleEdge(HImage gray, int width, int height, CircleRoiF roi, CaliperParameters cp, double angle, double outward, double inwardLength, out double row, out double col)
		{
			row = (col = 0.0);
			HTuple measureHandle = new HTuple();
			double num = Math.Cos(angle);
			double num2 = Math.Sin(angle);
			double num3 = Math.Max(1.0, (double)roi.Radius + outward);
			double num4 = Math.Max(0.0, (double)roi.Radius - inwardLength);
			double num5 = Math.Max(1.0, num3 - num4);
			double num6 = num3 - num5 * 0.5;
			double num7 = (double)roi.Center.Y + num2 * num6;
			double num8 = (double)roi.Center.X + num * num6;
			double num9 = Math.Atan2(num2, num);
			try
			{
				HOperatorSet.GenMeasureRectangle2(num7, num8, num9, num5 * 0.5, Math.Max(0.5, cp.MeasureWidth), width, height, string.IsNullOrWhiteSpace(cp.MeasureInterpolation) ? "bicubic" : cp.MeasureInterpolation, out measureHandle);
				HOperatorSet.MeasurePos(gray, measureHandle, cp.Sigma, cp.Threshold, string.IsNullOrWhiteSpace(cp.Transition) ? "all" : cp.Transition, "all", out var rowEdge, out var columnEdge, out var _, out var _);
				return TryPickOutermostCircleEdge(roi, rowEdge, columnEdge, num4, num3, out row, out col);
			}
			catch
			{
				return false;
			}
			finally
			{
				if (measureHandle != null && measureHandle.Length > 0)
				{
					try
					{
						HOperatorSet.CloseMeasure(measureHandle);
					}
					catch
					{
					}
				}
			}
		}

		private static bool TryPickOutermostCircleEdge(CircleRoiF roi, HTuple edgeRows, HTuple edgeCols, double minRadius, double maxRadius, out double row, out double col)
		{
			row = (col = 0.0);
			if (edgeRows == null || edgeCols == null || edgeRows.Length <= 0 || edgeCols.Length <= 0)
			{
				return false;
			}
			double num = double.NegativeInfinity;
			int num2 = -1;
			for (int i = 0; i < edgeRows.Length && i < edgeCols.Length; i++)
			{
				double num3 = edgeCols[i].D - (double)roi.Center.X;
				double num4 = edgeRows[i].D - (double)roi.Center.Y;
				double num5 = Math.Sqrt(num3 * num3 + num4 * num4);
				if (num5 >= minRadius - 2.0 && num5 <= maxRadius + 2.0 && num5 > num)
				{
					num = num5;
					num2 = i;
				}
			}
			if (num2 < 0)
			{
				for (int j = 0; j < edgeRows.Length && j < edgeCols.Length; j++)
				{
					double num6 = edgeCols[j].D - (double)roi.Center.X;
					double num7 = edgeRows[j].D - (double)roi.Center.Y;
					double num8 = Math.Sqrt(num6 * num6 + num7 * num7);
					if (num8 > num)
					{
						num = num8;
						num2 = j;
					}
				}
			}
			if (num2 < 0)
			{
				return false;
			}
			row = edgeRows[num2].D;
			col = edgeCols[num2].D;
			return true;	
		}

		private static bool TryFitCircleStable(HTuple rows, HTuple cols, out double row, out double col, out double radius, out string error)
		{
			row = (col = (radius = 0.0));
			error = "";
			if (rows == null || cols == null || rows.Length < 3 || cols.Length < 3)
			{
				error = "拟合点数量不足";
				return false;
			}
			HObject contour = null;
			try
			{
				HOperatorSet.GenContourPolygonXld(out contour, rows, cols);
				HOperatorSet.FitCircleContourXld(contour, "algebraic", -1, 0, 0, 3, 2, out var row2, out var column, out var radius2, out var _, out var _, out var _);
				if (row2.Length <= 0 || radius2.Length <= 0 || radius2[0].D <= 0.0)
				{
					error = "Halcon 未返回有效圆";
					return false;
				}
				row = row2[0].D;
				col = column[0].D;
				radius = radius2[0].D;
				return true;
			}
			catch (Exception ex)
			{
				error = ex.GetType().Name + ": " + ex.Message;
				return false;
			}
			finally
			{
				contour?.Dispose();
			}
		}

		private static void SelectInnerCirclePoints(CircleRoiF roi, HTuple rows, HTuple cols, out HTuple fitRows, out HTuple fitCols)
		{
			List<double> list = new List<double>();
			List<double> list2 = new List<double>();
			double num = Math.Max(1.0, roi.Radius);
			double num2 = Math.Max(1.0, num * 0.03);
			for (int i = 0; i < rows.Length && i < cols.Length; i++)
			{
				double num3 = cols[i].D - (double)roi.Center.X;
				double num4 = rows[i].D - (double)roi.Center.Y;
				double num5 = Math.Sqrt(num3 * num3 + num4 * num4);
				if (num5 <= num + num2)
				{
					list.Add(rows[i].D);
					list2.Add(cols[i].D);
				}
			}
			if (list.Count >= 3)
			{
				fitRows = new HTuple(list.ToArray());
				fitCols = new HTuple(list2.ToArray());
			}
			else
			{
				fitRows = rows;
				fitCols = cols;
			}
		}

		private static void SetCircleMetrologyParams(HTuple handle, HTuple idx, CaliperParameters cp)
		{
			HOperatorSet.SetMetrologyObjectParam(handle, idx, "num_measures", cp.NumMeasures);
			HOperatorSet.SetMetrologyObjectParam(handle, idx, "measure_interpolation", cp.MeasureInterpolation);
			HOperatorSet.SetMetrologyObjectParam(handle, idx, "measure_select", "all");
			HOperatorSet.SetMetrologyObjectParam(handle, idx, "measure_transition", string.IsNullOrWhiteSpace(cp.Transition) ? "all" : cp.Transition);
		}

		private static EdgeLineFit LineFromCircleCenters(PointF c1, PointF c2, int width, int height)
		{
			EdgeLineFit edgeLineFit = new EdgeLineFit
			{
				Success = false
			};
			double num = c2.X - c1.X;
			double num2 = c2.Y - c1.Y;
			double num3 = Math.Sqrt(num * num + num2 * num2);
			if (num3 < 1E-09)
			{
				edgeLineFit.Message = "两个圆心重合";
				return edgeLineFit;
			}
			double num4 = num / num3;
			double num5 = num2 / num3;
			if (ClipInfiniteLineToImage(c1.Y, c1.X, num5, num4, width, height, out var rA, out var cA, out var rB, out var cB))
			{
				edgeLineFit.P1 = new PointF((float)cA, (float)rA);
				edgeLineFit.P2 = new PointF((float)cB, (float)rB);
			}
			else
			{
				edgeLineFit.P1 = c1;
				edgeLineFit.P2 = c2;
			}
			edgeLineFit.AngleRad = Math.Atan2(num5, num4);
			edgeLineFit.Success = true;
			return edgeLineFit;
		}

		private static void SetMetrologyParams(HTuple handle, HTuple idx, CaliperParameters cp)
		{
			HOperatorSet.SetMetrologyObjectParam(handle, idx, "num_measures", cp.NumMeasures);
			HOperatorSet.SetMetrologyObjectParam(handle, idx, "measure_interpolation", cp.MeasureInterpolation);
			HOperatorSet.SetMetrologyObjectParam(handle, idx, "measure_select", string.IsNullOrWhiteSpace(cp.MeasureSelect) ? "first" : cp.MeasureSelect);
			HOperatorSet.SetMetrologyObjectParam(handle, idx, "measure_transition", string.IsNullOrWhiteSpace(cp.Transition) ? "all" : cp.Transition);
		}

		private static void NormalizeCaliper(CaliperParameters cp)
		{
			if (cp != null)
			{
				if (cp.NumMeasures < 1)
				{
					cp.NumMeasures = 1;
				}
				if (cp.MeasureLength < 1.0)
				{
					cp.MeasureLength = 1.0;
				}
				if (cp.MeasureWidth < 1.0)
				{
					cp.MeasureWidth = 1.0;
				}
				if (cp.Sigma <= 0.0)
				{
					cp.Sigma = 1.0;
				}
				if (cp.Threshold <= 0.0)
				{
					cp.Threshold = 1.0;
				}
				if (cp.SearchOutward < 0.0)
				{
					cp.SearchOutward = 0.0;
				}
				if (double.IsNaN(cp.LineFitNormalRatio) || double.IsInfinity(cp.LineFitNormalRatio) || cp.LineFitNormalRatio <= 0.0)
				{
					cp.LineFitNormalRatio = 0.60;
				}
				cp.LineFitNormalRatio = Math.Max(0.10, Math.Min(1.0, cp.LineFitNormalRatio));
				if (string.IsNullOrWhiteSpace(cp.MeasureInterpolation))
				{
					cp.MeasureInterpolation = "bicubic";
				}
				if (string.IsNullOrWhiteSpace(cp.MeasureSelect))
				{
					cp.MeasureSelect = "first";
				}
				if (string.IsNullOrWhiteSpace(cp.Transition))
				{
					cp.Transition = "negative";
				}
			}
		}

		private static bool TryFitLineByNormalRatio(IList<PointF> measurePoints, double normalRatio, double fallbackAngle, int width, int height, out double r1, out double c1, out double r2, out double c2, out double angle, out string error)
		{
			r1 = (c1 = (r2 = (c2 = (angle = 0.0))));
			error = "";
			List<PointF> list = ((measurePoints == null) ? new List<PointF>() : measurePoints.Where((PointF p) => !float.IsNaN(p.X) && !float.IsInfinity(p.X) && !float.IsNaN(p.Y) && !float.IsInfinity(p.Y)).ToList());
			if (list.Count < 2)
			{
				error = "拟合点数量不足";
				return false;
			}
			double num = normalRatio;
			if (double.IsNaN(num) || double.IsInfinity(num) || num <= 0.0)
			{
				num = 0.60;
			}
			num = Math.Max(0.10, Math.Min(1.0, num));
			int num2 = Math.Max(2, Math.Min(list.Count, (int)Math.Round((double)list.Count * num, MidpointRounding.AwayFromZero)));
			int num3 = Math.Min(64, list.Count);
			List<int> list2 = new List<int>(num3);
			for (int i = 0; i < num3; i++)
			{
				int item = (num3 == 1) ? 0 : (int)Math.Round((double)i * (double)(list.Count - 1) / (double)(num3 - 1), MidpointRounding.AwayFromZero);
				if (list2.Count == 0 || list2[list2.Count - 1] != item)
				{
					list2.Add(item);
				}
			}
			double num4 = double.PositiveInfinity;
			double num5 = -1.0;
			double num6 = 0.0;
			double num7 = 0.0;
			double num8 = Math.Sin(fallbackAngle);
			double num9 = Math.Cos(fallbackAngle);
			for (int j = 0; j < list2.Count; j++)
			{
				PointF pointF = list[list2[j]];
				for (int k = j + 1; k < list2.Count; k++)
				{
					PointF pointF2 = list[list2[k]];
					double num10 = (double)pointF2.X - pointF.X;
					double num11 = (double)pointF2.Y - pointF.Y;
					double num12 = num10 * num10 + num11 * num11;
					if (num12 <= 1E-12)
					{
						continue;
					}
					double num13 = Math.Sqrt(num12);
					double num14 = num11 / num13;
					double num15 = num10 / num13;
					double trimmedResidual = GetTrimmedSquaredResidual(list, pointF.Y, pointF.X, num14, num15, num2);
					double num16 = double.IsPositiveInfinity(num4) ? 0.0 : Math.Max(1.0, Math.Abs(num4)) * 1E-12;
					if (trimmedResidual < num4 - num16 || (Math.Abs(trimmedResidual - num4) <= num16 && num12 > num5))
					{
						num4 = trimmedResidual;
						num5 = num12;
						num6 = ((double)pointF.Y + pointF2.Y) * 0.5;
						num7 = ((double)pointF.X + pointF2.X) * 0.5;
						num8 = num14;
						num9 = num15;
					}
				}
			}
			if (double.IsPositiveInfinity(num4))
			{
				num6 = list.Average((PointF p) => (double)p.Y);
				num7 = list.Average((PointF p) => (double)p.X);
			}
			for (int l = 0; l < 4; l++)
			{
				int[] closestPointIndexes = GetClosestPointIndexes(list, num6, num7, num8, num9, num2);
				if (!TryFitLineTls(list, closestPointIndexes, out var rowCenter, out var colCenter, out var directionRow, out var directionCol))
				{
					break;
				}
				if (directionRow * Math.Sin(fallbackAngle) + directionCol * Math.Cos(fallbackAngle) < 0.0)
				{
					directionRow = 0.0 - directionRow;
					directionCol = 0.0 - directionCol;
				}
				num6 = rowCenter;
				num7 = colCenter;
				num8 = directionRow;
				num9 = directionCol;
			}
			double num17 = Math.Sqrt(num8 * num8 + num9 * num9);
			if (num17 < 1E-12)
			{
				num8 = Math.Sin(fallbackAngle);
				num9 = Math.Cos(fallbackAngle);
			}
			else
			{
				num8 /= num17;
				num9 /= num17;
			}
			angle = Math.Atan2(num8, num9);
			if (!ClipInfiniteLineToImage(num6, num7, num8, num9, width, height, out r1, out c1, out r2, out c2))
			{
				double num18 = Math.Max(1.0, Math.Max(width, height));
				r1 = num6 - num8 * num18;
				c1 = num7 - num9 * num18;
				r2 = num6 + num8 * num18;
				c2 = num7 + num9 * num18;
			}
			return true;
		}

		private static double GetTrimmedSquaredResidual(IList<PointF> points, double rowCenter, double colCenter, double directionRow, double directionCol, int keepCount)
		{
			double[] array = new double[points.Count];
			for (int i = 0; i < points.Count; i++)
			{
				double num = ((double)points[i].X - colCenter) * directionRow - ((double)points[i].Y - rowCenter) * directionCol;
				array[i] = num * num;
			}
			Array.Sort(array);
			double num2 = 0.0;
			for (int j = 0; j < keepCount; j++)
			{
				num2 += array[j];
			}
			return num2 / (double)keepCount;
		}

		private static int[] GetClosestPointIndexes(IList<PointF> points, double rowCenter, double colCenter, double directionRow, double directionCol, int keepCount)
		{
			List<KeyValuePair<double, int>> list = new List<KeyValuePair<double, int>>(points.Count);
			for (int i = 0; i < points.Count; i++)
			{
				double num = ((double)points[i].X - colCenter) * directionRow - ((double)points[i].Y - rowCenter) * directionCol;
				list.Add(new KeyValuePair<double, int>(num * num, i));
			}
			list.Sort(delegate(KeyValuePair<double, int> a, KeyValuePair<double, int> b)
			{
				int num2 = a.Key.CompareTo(b.Key);
				return (num2 != 0) ? num2 : a.Value.CompareTo(b.Value);
			});
			int[] array = new int[keepCount];
			for (int j = 0; j < keepCount; j++)
			{
				array[j] = list[j].Value;
			}
			return array;
		}

		private static bool TryFitLineTls(IList<PointF> points, IList<int> indexes, out double rowCenter, out double colCenter, out double directionRow, out double directionCol)
		{
			rowCenter = (colCenter = (directionRow = (directionCol = 0.0)));
			if (points == null || indexes == null || indexes.Count < 2)
			{
				return false;
			}
			for (int i = 0; i < indexes.Count; i++)
			{
				PointF pointF = points[indexes[i]];
				rowCenter += pointF.Y;
				colCenter += pointF.X;
			}
			rowCenter /= indexes.Count;
			colCenter /= indexes.Count;
			double num = 0.0;
			double num2 = 0.0;
			double num3 = 0.0;
			for (int j = 0; j < indexes.Count; j++)
			{
				PointF pointF2 = points[indexes[j]];
				double num4 = (double)pointF2.X - colCenter;
				double num5 = (double)pointF2.Y - rowCenter;
				num += num4 * num4;
				num2 += num4 * num5;
				num3 += num5 * num5;
			}
			if (num + num3 <= 1E-12)
			{
				return false;
			}
			double num6 = 0.5 * Math.Atan2(2.0 * num2, num - num3);
			directionCol = Math.Cos(num6);
			directionRow = Math.Sin(num6);
			return true;
		}

		private static bool TryFitLineStable(HTuple rows, HTuple cols, int width, int height, out double r1, out double c1, out double r2, out double c2, out double angle, out string error)
		{
			r1 = (c1 = (r2 = (c2 = (angle = 0.0))));
			error = "";
			if (rows == null || cols == null || rows.Length < 2 || cols.Length < 2)
			{
				error = "拟合点数量不足";
				return false;
			}
			HObject contour = null;
			try
			{
				HOperatorSet.GenContourPolygonXld(out contour, rows, cols);
				HOperatorSet.FitLineContourXld(contour, "tukey", -1, 0, 5, 2, out var rowBegin, out var colBegin, out var rowEnd, out var colEnd, out var _, out var _, out var _);
				if (rowBegin.Length == 0)
				{
					error = "Halcon 未返回有效拟合线";
					return false;
				}
				double d = rowBegin[0].D;
				double d2 = colBegin[0].D;
				double d3 = rowEnd[0].D;
				double d4 = colEnd[0].D;
				double num = d3 - d;
				double num2 = d4 - d2;
				double num3 = Math.Sqrt(num * num + num2 * num2);
				if (num3 < 1E-09)
				{
					error = "拟合线长度过短";
					return false;
				}
				num /= num3;
				num2 /= num3;
				angle = Math.Atan2(num, num2);
				if (!ClipInfiniteLineToImage((d + d3) * 0.5, (d2 + d4) * 0.5, num, num2, width, height, out r1, out c1, out r2, out c2))
				{
					r1 = d;
					c1 = d2;
					r2 = d3;
					c2 = d4;
				}
				return true;
			}
			catch (Exception ex)
			{
				error = ex.GetType().Name + ": " + ex.Message;
				return false;
			}
			finally
			{
				contour?.Dispose();
			}
		}

		private static bool ClipInfiniteLineToImage(double r0, double c0, double dr, double dc, int width, int height, out double rA, out double cA, out double rB, out double cB)
		{
			rA = (cA = (rB = (cB = 0.0)));
			double w = width - 1;
			double h = height - 1;
			List<Tuple<double, double>> pts = new List<Tuple<double, double>>();
			if (Math.Abs(dc) > 1E-12)
			{
				double num = (0.0 - c0) / dc;
				Add(r0 + num * dr, 0.0);
				num = (w - c0) / dc;
				Add(r0 + num * dr, w);
			}
			if (Math.Abs(dr) > 1E-12)
			{
				double num2 = (0.0 - r0) / dr;
				Add(0.0, c0 + num2 * dc);
				num2 = (h - r0) / dr;
				Add(h, c0 + num2 * dc);
			}
			if (pts.Count < 2)
			{
				return false;
			}
			double num3 = -1.0;
			int index = 0;
			int index2 = 1;
			for (int i = 0; i < pts.Count; i++)
			{
				for (int j = i + 1; j < pts.Count; j++)
				{
					double num4 = Math.Pow(pts[i].Item1 - pts[j].Item1, 2.0) + Math.Pow(pts[i].Item2 - pts[j].Item2, 2.0);
					if (num4 > num3)
					{
						num3 = num4;
						index = i;
						index2 = j;
					}
				}
			}
			rA = pts[index].Item1;
			cA = pts[index].Item2;
			rB = pts[index2].Item1;
			cB = pts[index2].Item2;
			return true;
			void Add(double num5, double num6)
			{
				if (num5 >= 0.0 && num5 <= h && num6 >= 0.0 && num6 <= w)
				{
					pts.Add(Tuple.Create(num5, num6));
				}
			}
		}

		private static EdgeLineFit CopyLine(EdgeLineFit src)
		{
			if (src == null)
			{
				return new EdgeLineFit();
			}
			EdgeLineFit edgeLineFit = new EdgeLineFit
			{
				Success = src.Success,
				P1 = src.P1,
				P2 = src.P2,
				AngleRad = src.AngleRad,
				Message = src.Message
			};
			foreach (PointF measurePoint in src.MeasurePoints)
			{
				edgeLineFit.MeasurePoints.Add(measurePoint);
			}
			return edgeLineFit;
		}

		private static HImage BitmapToGrayHImage(Bitmap bmp, out byte[] gray, out int w, out int h, out GCHandle grayHandle)
		{
			w = bmp.Width;
			h = bmp.Height;
			using (Bitmap bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb))
			{
				using (Graphics graphics = Graphics.FromImage(bitmap))
				{
					graphics.DrawImage(bmp, 0, 0, w, h);
				}
				Rectangle rect = new Rectangle(0, 0, w, h);
				BitmapData bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
				try
				{
					int stride = bitmapData.Stride;
					byte[] array = new byte[stride * h];
					Marshal.Copy(bitmapData.Scan0, array, 0, array.Length);
					gray = new byte[w * h];
					for (int i = 0; i < h; i++)
					{
						int num = i * stride;
						int num2 = i * w;
						for (int j = 0; j < w; j++)
						{
							int num3 = num + j * 3;
							gray[num2 + j] = (byte)(0.114 * (double)(int)array[num3] + 0.587 * (double)(int)array[num3 + 1] + 0.299 * (double)(int)array[num3 + 2]);
						}
					}
					grayHandle = GCHandle.Alloc(gray, GCHandleType.Pinned);
					HImage hImage = new HImage();
					hImage.GenImage1("byte", w, h, grayHandle.AddrOfPinnedObject());
					return hImage;
				}
				finally
				{
					bitmap.UnlockBits(bitmapData);
				}
			}
		}
	}
}
