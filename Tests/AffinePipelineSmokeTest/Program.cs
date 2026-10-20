using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using EdgeAlignInspect;

namespace EdgeAlignInspect.Tests
{
	internal static class Program
	{
		private const float ReferenceX = 280f;
		private const float ReferenceY = 210f;
		private const float ShiftX = 34f;
		private const float ShiftY = -21f;
		private const float RotationRad = 0.12f;

		[STAThread]
		private static int Main()
		{
			try
			{
				using (Bitmap reference = CreateReferenceImage())
				using (Bitmap runtime = TransformImage(reference, ShiftX, ShiftY, RotationRad))
				using (TemplateEdgeInspectProcessor processor = new TemplateEdgeInspectProcessor())
				{
					EdgeInspectJob job = CreateJob();
					job.TeachData = processor.Teach(reference, job);

					EdgeInspectResult referenceResult = processor.Inspect(reference, job.DeepClone());
					AssertMatch(referenceResult, "reference");
					AssertRoiNear(job.TemplateRoi, referenceResult.TemplateRoiCur, 0.15f, 0.002f, "reference template ROI");

					// Simulate a Job saved by the affected version. Headless execution must not
					// interpret template-reference coordinates as fixed image coordinates.
					job.BaseRois[0].UseTemplateTransform = false;
					job.CircleBaseRois[0].UseTemplateTransform = false;
					job.CirclePointRois[0].UseTemplateTransform = false;
					job.DetectItems[0].UseTemplateTransform = false;
					EdgeInspectJob savedJob = RoundTripJob(job);
					byte[] savedJobBeforeRun = SerializeJob(savedJob);
					EdgeInspectionToleranceOptions options = CreateOptions();
					TemplateEdgeInspectSdk sdk = new TemplateEdgeInspectSdk();
					EdgeInspectResult headlessResult = sdk.RunInspection(runtime, savedJob, options, false);
					AssertMatch(headlessResult, "headless");

					EdgeInspectResult directResult = processor.Inspect(runtime, savedJob);
					AssertMatch(directResult, "direct processor");
					AssertEquivalentRois(headlessResult, directResult, "headless/direct processor");

					EdgeInspectResult resultImageResult = sdk.RunInspection(runtime, savedJob, options, true);
					try
					{
						AssertMatch(resultImageResult, "headless with result image");
						AssertEquivalentRois(headlessResult, resultImageResult, "result-image off/on");
						if (resultImageResult.ResultImage == null || resultImageResult.ResultImage.Width <= 0 || resultImageResult.ResultImage.Height <= 0)
						{
							throw new InvalidOperationException("result-image path returned an empty image.");
						}
					}
					finally
					{
						resultImageResult.ResultImage?.Dispose();
					}

					EdgeInspectResult mtaResult = RunOnMtaThread(delegate
					{
						return sdk.RunInspection(runtime, savedJob, options, false);
					});
					AssertMatch(mtaResult, "MTA headless");
					AssertEquivalentRois(headlessResult, mtaResult, "STA/MTA headless");

					EdgeInspectResult repeatedResult = sdk.RunInspection(runtime, savedJob, options, false);
					AssertMatch(repeatedResult, "repeated headless");
					AssertEquivalentRois(headlessResult, repeatedResult, "repeated headless");
					if (!savedJobBeforeRun.SequenceEqual(SerializeJob(savedJob)))
					{
						throw new InvalidOperationException("inspection mutated the caller's Job.");
					}

					EdgeInspectJob pageJob;
					EdgeInspectResult pageResult;
					using (Form1 form = new Form1())
					{
						form.SetImage(runtime);
						form.SetJob(savedJob);
						FieldInfo canvasField = typeof(Form1).GetField("canvas", BindingFlags.Instance | BindingFlags.NonPublic);
						if (canvasField == null)
						{
							throw new InvalidOperationException("Form canvas was not found.");
						}
						HalconCanvas pageCanvas = (HalconCanvas)canvasField.GetValue(form);
						AssertRoiNear(headlessResult.TemplateRoiCur, pageCanvas.TemplateRoi, 0.02f, 0.0002f, "page edit template ROI");
						AssertRoiNear(headlessResult.BaseResults.Single().RoiCur, pageCanvas.BaseRois.Single(), 0.02f, 0.0002f, "page edit base ROI");
						AssertCircleNear(headlessResult.CircleBaseResults.Single().Circle1RoiCur, pageCanvas.CircleBaseRois.Single().Circle1, 0.02f, "page edit circle-base ROI 1");
						AssertCircleNear(headlessResult.CircleBaseResults.Single().Circle2RoiCur, pageCanvas.CircleBaseRois.Single().Circle2, 0.02f, "page edit circle-base ROI 2");
						AssertCircleNear(headlessResult.CirclePointResults.Single().CircleRoiCur, pageCanvas.CirclePointRois.Single(), 0.02f, "page edit circle-point ROI");
						AssertRoiNear(headlessResult.DetectResults.Single().RoiCur, pageCanvas.DetectRois.Single(), 0.02f, 0.0002f, "page edit detect ROI");
						MethodInfo buildJob = typeof(Form1).GetMethod("BuildJobForReturn", BindingFlags.Instance | BindingFlags.NonPublic);
						if (buildJob == null)
						{
							throw new InvalidOperationException("BuildJobForReturn was not found.");
						}
						pageJob = (EdgeInspectJob)buildJob.Invoke(form, null);
						pageResult = form.RunInspectionForSdk(pageJob, options, false);
					}
					AssertMatch(pageResult, "page");

					RotRectF expectedDetect = TransformRoi(job.DetectItems[0].Roi, ShiftX, ShiftY, RotationRad);
					RotRectF expectedBase = TransformRoi(job.BaseRois[0].Roi, ShiftX, ShiftY, RotationRad);
					CircleRoiF expectedCircle1 = TransformCircle(job.CircleBaseRois[0].Circle1, ShiftX, ShiftY, RotationRad);
					CircleRoiF expectedCircle2 = TransformCircle(job.CircleBaseRois[0].Circle2, ShiftX, ShiftY, RotationRad);
					CircleRoiF expectedCirclePoint = TransformCircle(job.CirclePointRois[0].Circle, ShiftX, ShiftY, RotationRad);
					RotRectF headlessDetect = headlessResult.DetectResults.Single().RoiCur;
					RotRectF pageDetect = pageResult.DetectResults.Single().RoiCur;
					AssertRoiNear(expectedDetect, headlessDetect, 3.0f, 0.03f, "headless transformed detect ROI");
					AssertRoiNear(expectedBase, headlessResult.BaseResults.Single().RoiCur, 3.0f, 0.03f, "headless transformed base ROI");
					AssertCircleNear(expectedCircle1, headlessResult.CircleBaseResults.Single().Circle1RoiCur, 3.0f, "headless transformed circle-base ROI 1");
					AssertCircleNear(expectedCircle2, headlessResult.CircleBaseResults.Single().Circle2RoiCur, 3.0f, "headless transformed circle-base ROI 2");
					AssertCircleNear(expectedCirclePoint, headlessResult.CirclePointResults.Single().CircleRoiCur, 3.0f, "headless transformed circle-point ROI");
					AssertRoiNear(headlessResult.TemplateRoiCur, pageResult.TemplateRoiCur, 0.02f, 0.0002f, "page/headless template ROI");
					AssertRoiNear(headlessDetect, pageDetect, 0.02f, 0.0002f, "page/headless detect ROI");
					AssertRoiNear(headlessResult.BaseResults.Single().RoiCur, pageResult.BaseResults.Single().RoiCur, 0.02f, 0.0002f, "page/headless base ROI");
					AssertCircleNear(headlessResult.CircleBaseResults.Single().Circle1RoiCur, pageResult.CircleBaseResults.Single().Circle1RoiCur, 0.02f, "page/headless circle-base ROI 1");
					AssertCircleNear(headlessResult.CircleBaseResults.Single().Circle2RoiCur, pageResult.CircleBaseResults.Single().Circle2RoiCur, 0.02f, "page/headless circle-base ROI 2");
					AssertCircleNear(headlessResult.CirclePointResults.Single().CircleRoiCur, pageResult.CirclePointResults.Single().CircleRoiCur, 0.02f, "page/headless circle-point ROI");

					EdgeInspectJob searchJob = savedJob.DeepClone();
					searchJob.UseTemplateSearchRoi = true;
					searchJob.TemplateSearchRoi = new RotRectF(new PointF(320f, 240f), 0f, 300f, 220f);
					EdgeInspectResult searchResult = sdk.RunInspection(runtime, searchJob, options, false);
					AssertMatch(searchResult, "search-ROI headless");
					AssertRoiNear(searchJob.TemplateSearchRoi, searchResult.TemplateSearchRoiCur, 0.001f, 0.00001f, "search ROI remains in image coordinates");
					AssertRoiNear(expectedDetect, searchResult.DetectResults.Single().RoiCur, 3.0f, 0.03f, "search-ROI transformed detect ROI");

					EdgeInspectJob noMatchJob = CreateJob();
					noMatchJob.Match.Enabled = false;
					noMatchJob.UseTemplateSearchRoi = false;
					EdgeInspectResult noMatchResult = sdk.RunInspection(runtime, noMatchJob, options, false);
					AssertMatch(noMatchResult, "template-disabled headless");
					AssertRoiNear(noMatchJob.TemplateRoi, noMatchResult.TemplateRoiCur, 0.001f, 0.00001f, "template-disabled template ROI");
					AssertRoiNear(noMatchJob.BaseRois[0].Roi, noMatchResult.BaseResults.Single().RoiCur, 0.001f, 0.00001f, "template-disabled base ROI");
					AssertCircleNear(noMatchJob.CircleBaseRois[0].Circle1, noMatchResult.CircleBaseResults.Single().Circle1RoiCur, 0.001f, "template-disabled circle-base ROI 1");
					AssertCircleNear(noMatchJob.CircleBaseRois[0].Circle2, noMatchResult.CircleBaseResults.Single().Circle2RoiCur, 0.001f, "template-disabled circle-base ROI 2");
					AssertCircleNear(noMatchJob.CirclePointRois[0].Circle, noMatchResult.CirclePointResults.Single().CircleRoiCur, 0.001f, "template-disabled circle-point ROI");
					AssertRoiNear(noMatchJob.DetectItems[0].Roi, noMatchResult.DetectResults.Single().RoiCur, 0.001f, 0.00001f, "template-disabled detect ROI");

					EdgeInspectJob contourJob = CreateJob();
					contourJob.Match.UseOuterContourOnly = true;
					contourJob.TeachData = processor.Teach(reference, contourJob);
					EdgeInspectResult contourReferenceResult = processor.Inspect(reference, contourJob.DeepClone());
					AssertMatch(contourReferenceResult, "outer-contour reference");
					AssertRoiNear(contourJob.TemplateRoi, contourReferenceResult.TemplateRoiCur, 0.15f, 0.002f, "outer-contour reference template ROI");
					contourJob.BaseRois[0].UseTemplateTransform = false;
					contourJob.CircleBaseRois[0].UseTemplateTransform = false;
					contourJob.CirclePointRois[0].UseTemplateTransform = false;
					contourJob.DetectItems[0].UseTemplateTransform = false;
					EdgeInspectJob savedContourJob = RoundTripJob(contourJob);
					EdgeInspectResult contourHeadlessResult = sdk.RunInspection(runtime, savedContourJob, options, false);
					AssertMatch(contourHeadlessResult, "outer-contour headless");
					AssertRoiNear(expectedDetect, contourHeadlessResult.DetectResults.Single().RoiCur, 3.0f, 0.03f, "outer-contour transformed detect ROI");
					using (Form1 contourForm = new Form1())
					{
						contourForm.SetImage(runtime);
						contourForm.SetJob(savedContourJob);
						MethodInfo buildContourJob = typeof(Form1).GetMethod("BuildJobForReturn", BindingFlags.Instance | BindingFlags.NonPublic);
						EdgeInspectJob contourPageJob = (EdgeInspectJob)buildContourJob.Invoke(contourForm, null);
						EdgeInspectResult contourPageResult = contourForm.RunInspectionForSdk(contourPageJob, options, false);
						AssertMatch(contourPageResult, "outer-contour page");
						AssertRoiNear(contourHeadlessResult.TemplateRoiCur, contourPageResult.TemplateRoiCur, 0.02f, 0.0002f, "outer-contour page/headless template ROI");
						AssertRoiNear(contourHeadlessResult.DetectResults.Single().RoiCur, contourPageResult.DetectResults.Single().RoiCur, 0.02f, 0.0002f, "outer-contour page/headless detect ROI");
					}

					Console.WriteLine("PASS");
					Console.WriteLine("Reference pose: row={0:F4}, col={1:F4}, angle={2:F6}", job.TeachData.RefRow, job.TeachData.RefCol, job.TeachData.RefAngle);
					Console.WriteLine("Headless match: row={0:F4}, col={1:F4}, angle={2:F6}", headlessResult.TemplateMatchRow, headlessResult.TemplateMatchCol, headlessResult.TemplateMatchAngle);
					Console.WriteLine("Detect ROI: x={0:F3}, y={1:F3}, angle={2:F6}", headlessDetect.Center.X, headlessDetect.Center.Y, headlessDetect.AngleRad);
					Console.WriteLine("Outer-contour reference pose: row={0:F4}, col={1:F4}, angle={2:F6}", contourJob.TeachData.RefRow, contourJob.TeachData.RefCol, contourJob.TeachData.RefAngle);
					return 0;
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("FAIL: " + ex);
				return 1;
			}
		}

		private static EdgeInspectJob CreateJob()
		{
			EdgeInspectJob job = new EdgeInspectJob
			{
				TemplateRoi = new RotRectF(new PointF(ReferenceX, ReferenceY), 0f, 135f, 105f),
				UseReferenceLine = false
			};
			job.Match.Enabled = true;
			job.Match.UseOuterContourOnly = false;
			job.Match.MinScore = 0.45;
			job.Match.AngleStart = -0.35;
			job.Match.AngleExtent = 0.70;
			job.UseTemplateSearchRoi = false;
			job.BaseRois.Add(new BaseRoiItem
			{
				Name = "Base1",
				Roi = new RotRectF(new PointF(215f, 236f), 0f, 45f, 16f),
				Caliper = EdgeInspectJob.CreateDefaultBaseCaliper()
			});
			job.CircleBaseRois.Add(new CircleBaseRoiItem
			{
				Name = "CircleBase1",
				Circle1 = new CircleRoiF(new PointF(225f, 165f), 18f),
				Circle2 = new CircleRoiF(new PointF(330f, 165f), 18f),
				Caliper = EdgeInspectJob.CreateDefaultCircleCaliper()
			});
			job.CirclePointRois.Add(new CirclePointRoiItem
			{
				Name = "CirclePoint1",
				Circle = new CircleRoiF(new PointF(335f, 248f), 20f),
				Caliper = EdgeInspectJob.CreateDefaultCircleCaliper()
			});
			job.DetectItems.Add(new DetectRoiItem
			{
				Name = "Detect1",
				Enabled = true,
				UseReferenceLine = false,
				AngleReference = DetectAngleReferenceMode.Vertical,
				Roi = new RotRectF(new PointF(365f, 215f), 0f, 58f, 24f),
				Caliper = EdgeInspectJob.CreateDefaultDetectCaliper()
			});
			job.Normalize();
			return job;
		}

		private static EdgeInspectionToleranceOptions CreateOptions()
		{
			return new EdgeInspectionToleranceOptions
			{
				BurrToleranceMm = 2.0,
				DentToleranceMm = 2.0,
				OverEdgeToleranceMm = 2.0,
				CopperLeakToleranceMm = 2.0,
				PixelResolutionX = 1.0,
				PixelResolutionY = 1.0
			};
		}

		private static EdgeInspectJob RoundTripJob(EdgeInspectJob job)
		{
			using (MemoryStream stream = new MemoryStream(SerializeJob(job)))
			{
				BinaryFormatter formatter = new BinaryFormatter();
				return (EdgeInspectJob)formatter.Deserialize(stream);
			}
		}

		private static byte[] SerializeJob(EdgeInspectJob job)
		{
			using (MemoryStream stream = new MemoryStream())
			{
				BinaryFormatter formatter = new BinaryFormatter();
				formatter.Serialize(stream, job);
				return stream.ToArray();
			}
		}

		private static EdgeInspectResult RunOnMtaThread(Func<EdgeInspectResult> action)
		{
			EdgeInspectResult result = null;
			Exception failure = null;
			Thread thread = new Thread(new ThreadStart(delegate
			{
				try
				{
					result = action();
				}
				catch (Exception ex)
				{
					failure = ex;
				}
			}));
			thread.SetApartmentState(ApartmentState.MTA);
			thread.Start();
			if (!thread.Join(TimeSpan.FromSeconds(60)))
			{
				throw new TimeoutException("MTA headless inspection did not finish within 60 seconds.");
			}
			if (failure != null)
			{
				throw new InvalidOperationException("MTA headless inspection failed.", failure);
			}
			return result;
		}

		private static Bitmap CreateReferenceImage()
		{
			Bitmap image = new Bitmap(640, 480);
			using (Graphics graphics = Graphics.FromImage(image))
			{
				graphics.Clear(Color.Black);
				graphics.SmoothingMode = SmoothingMode.None;
				using (Brush white = new SolidBrush(Color.White))
				using (Brush gray = new SolidBrush(Color.FromArgb(140, 140, 140)))
				{
					graphics.FillRectangle(white, 185, 135, 190, 55);
					graphics.FillRectangle(white, 185, 190, 62, 118);
					graphics.FillRectangle(gray, 286, 207, 74, 83);
					graphics.FillEllipse(white, 315, 145, 31, 31);
				}
			}
			return image;
		}

		private static Bitmap TransformImage(Bitmap source, float shiftX, float shiftY, float angleRad)
		{
			Bitmap result = new Bitmap(source.Width, source.Height);
			float cos = (float)Math.Cos(angleRad);
			float sin = (float)Math.Sin(angleRad);
			float dx = ReferenceX + shiftX - cos * ReferenceX + sin * ReferenceY;
			float dy = ReferenceY + shiftY - sin * ReferenceX - cos * ReferenceY;
			using (Graphics graphics = Graphics.FromImage(result))
			using (Matrix transform = new Matrix(cos, sin, -sin, cos, dx, dy))
			{
				graphics.Clear(Color.Black);
				graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
				graphics.Transform = transform;
				graphics.DrawImageUnscaled(source, 0, 0);
			}
			return result;
		}

		private static RotRectF TransformRoi(RotRectF roi, float shiftX, float shiftY, float angleRad)
		{
			float cos = (float)Math.Cos(angleRad);
			float sin = (float)Math.Sin(angleRad);
			float localX = roi.Center.X - ReferenceX;
			float localY = roi.Center.Y - ReferenceY;
			PointF center = new PointF(
				ReferenceX + shiftX + cos * localX - sin * localY,
				ReferenceY + shiftY + sin * localX + cos * localY);
			return new RotRectF(center, roi.AngleRad + angleRad, roi.HalfLen1, roi.HalfLen2);
		}

		private static CircleRoiF TransformCircle(CircleRoiF roi, float shiftX, float shiftY, float angleRad)
		{
			RotRectF transformed = TransformRoi(new RotRectF(roi.Center, 0f, 1f, 1f), shiftX, shiftY, angleRad);
			return new CircleRoiF(transformed.Center, roi.Radius);
		}

		private static void AssertMatch(EdgeInspectResult result, string path)
		{
			if (result == null || !result.TemplateMatchOk)
			{
				throw new InvalidOperationException(path + " template match failed: " + (result == null ? "null result" : result.Message));
			}
			if (result.DetectResults.Count != 1)
			{
				throw new InvalidOperationException(path + " returned " + result.DetectResults.Count + " detect results.");
			}
		}

		private static void AssertEquivalentRois(EdgeInspectResult expected, EdgeInspectResult actual, string name)
		{
			AssertRoiNear(expected.TemplateRoiCur, actual.TemplateRoiCur, 0.02f, 0.0002f, name + " template ROI");
			AssertRoiNear(expected.DetectResults.Single().RoiCur, actual.DetectResults.Single().RoiCur, 0.02f, 0.0002f, name + " detect ROI");
			AssertRoiNear(expected.BaseResults.Single().RoiCur, actual.BaseResults.Single().RoiCur, 0.02f, 0.0002f, name + " base ROI");
			AssertCircleNear(expected.CircleBaseResults.Single().Circle1RoiCur, actual.CircleBaseResults.Single().Circle1RoiCur, 0.02f, name + " circle-base ROI 1");
			AssertCircleNear(expected.CircleBaseResults.Single().Circle2RoiCur, actual.CircleBaseResults.Single().Circle2RoiCur, 0.02f, name + " circle-base ROI 2");
			AssertCircleNear(expected.CirclePointResults.Single().CircleRoiCur, actual.CirclePointResults.Single().CircleRoiCur, 0.02f, name + " circle-point ROI");
		}

		private static void AssertRoiNear(RotRectF expected, RotRectF actual, float centerTolerance, float angleTolerance, string name)
		{
			float dx = expected.Center.X - actual.Center.X;
			float dy = expected.Center.Y - actual.Center.Y;
			float centerError = (float)Math.Sqrt(dx * dx + dy * dy);
			float angleError = Math.Abs(RotRectF.NormalizeAngle(expected.AngleRad - actual.AngleRad));
			if (centerError > centerTolerance || angleError > angleTolerance)
			{
				throw new InvalidOperationException(string.Format(
					"{0} mismatch: center error={1:F4}px, angle error={2:F6}rad; expected=({3:F3},{4:F3},{5:F6}), actual=({6:F3},{7:F3},{8:F6})",
					name, centerError, angleError,
					expected.Center.X, expected.Center.Y, expected.AngleRad,
					actual.Center.X, actual.Center.Y, actual.AngleRad));
			}
		}

		private static void AssertCircleNear(CircleRoiF expected, CircleRoiF actual, float centerTolerance, string name)
		{
			float dx = expected.Center.X - actual.Center.X;
			float dy = expected.Center.Y - actual.Center.Y;
			float centerError = (float)Math.Sqrt(dx * dx + dy * dy);
			float radiusError = Math.Abs(expected.Radius - actual.Radius);
			if (centerError > centerTolerance || radiusError > 0.001f)
			{
				throw new InvalidOperationException(string.Format(
					"{0} mismatch: center error={1:F4}px, radius error={2:F4}px; expected=({3:F3},{4:F3},{5:F3}), actual=({6:F3},{7:F3},{8:F3})",
					name, centerError, radiusError,
					expected.Center.X, expected.Center.Y, expected.Radius,
					actual.Center.X, actual.Center.Y, actual.Radius));
			}
		}
	}
}
