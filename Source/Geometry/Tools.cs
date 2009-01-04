
#region ================== Copyright (c) 2007 Pascal vd Heiden

/*
 * Copyright (c) 2007 Pascal vd Heiden, www.codeimp.com
 * This program is released under GNU General Public License
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 */

#endregion

#region ================== Namespaces

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CodeImp.DoomBuilder.Geometry;
using CodeImp.DoomBuilder.Rendering;
using SlimDX.Direct3D9;
using System.Drawing;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.IO;

#endregion

namespace CodeImp.DoomBuilder.Geometry
{
	/// <summary>
	/// Tools to work with geometry.
	/// </summary>
	public static class Tools
	{
		#region ================== Structures

		private struct SidedefSettings
		{
			public string newtexhigh;
			public string newtexmid;
			public string newtexlow;
		}

		private struct SidedefAlignJob
		{
			public Sidedef sidedef;
			
			public int offsetx;

			// This is an absolute height in world space. Subtract the
			// ceiling height to get the correct Y offset.
			public int offsety;

			// When this is true, the previous sidedef was on the left of
			// this one and the texture X offset of this sidedef can be set
			// directly. When this is false, the length of this sidedef
			// must be subtracted from the X offset first.
			public bool forward;
		}
		
		#endregion
		
		#region ================== Constants
		
		#endregion

		#region ================== Polygons and Triangles

		// Point inside the polygon?
		// See: http://local.wasp.uwa.edu.au/~pbourke/geometry/insidepoly/
		public static bool PointInPolygon(ICollection<Vector2D> polygon, Vector2D point)
		{
			Vector2D v1 = General.GetByIndex(polygon, polygon.Count - 1);
			uint c = 0;

			// Go for all vertices
			foreach(Vector2D v2 in polygon)
			{
				// Determine min/max values
				float miny = Math.Min(v1.y, v2.y);
				float maxy = Math.Max(v1.y, v2.y);
				float maxx = Math.Max(v1.x, v2.x);

				// Check for intersection
				if((point.y > miny) && (point.y <= maxy))
				{
					if(point.x <= maxx)
					{
						if(v1.y != v2.y)
						{
							float xint = (point.y - v1.y) * (v2.x - v1.x) / (v2.y - v1.y) + v1.x;
							if((v1.x == v2.x) || (point.x <= xint)) c++;
						}
					}
				}

				// Move to next
				v1 = v2;
			}

			// Inside this polygon?
			return (c & 0x00000001UL) != 0;
		}
		
		#endregion
		
		#region ================== Pathfinding

		/// <summary>
		/// This finds a potential sector at the given coordinates,
		/// or returns null when a sector is not possible there.
		/// </summary>
		public static List<LinedefSide> FindPotentialSectorAt(Vector2D pos)
		{
			// Find the nearest line and determine side, then use the other method to create the sector
			Linedef l = General.Map.Map.NearestLinedef(pos);
			return FindPotentialSectorAt(l, (l.SideOfLine(pos) <= 0));
		}

		/// <summary>
		/// This finds a potential sector starting at the given line and side,
		/// or returns null when sector is not possible.
		/// </summary>
		public static List<LinedefSide> FindPotentialSectorAt(Linedef line, bool front)
		{
			List<LinedefSide> alllines = new List<LinedefSide>();
			
			// Find the outer lines
			EarClipPolygon p = FindOuterLines(line, front, alllines);
			if(p != null)
			{
				// Find the inner lines
				FindInnerLines(p, alllines);
				return alllines;
			}
			else
				return null;
		}

		// This finds the inner lines of the sector and adds them to the sector polygon
		private static void FindInnerLines(EarClipPolygon p, List<LinedefSide> alllines)
		{
			Vertex foundv;
			bool vvalid, findmore;
			Linedef foundline;
			float foundangle = 0f;
			bool foundlinefront;
			RectangleF bbox = p.CreateBBox();
			
			do
			{
				findmore = false;

				// Go for all vertices to find the right-most vertex inside the polygon
				foundv = null;
				foreach(Vertex v in General.Map.Map.Vertices)
				{
					// Inside the polygon bounding box?
					if((v.Position.x >= bbox.Left) && (v.Position.x <= bbox.Right) &&
					   (v.Position.y >= bbox.Top) && (v.Position.y <= bbox.Bottom))
					{
						// More to the right?
						if((foundv == null) || (v.Position.x >= foundv.Position.x))
						{
							// Vertex is inside the polygon?
							if(p.Intersect(v.Position))
							{
								// Vertex has lines attached?
								if(v.Linedefs.Count > 0)
								{
									// Go for all lines to see if the vertex is not of the polygon itsself
									vvalid = true;
									foreach(LinedefSide ls in alllines)
									{
										if((ls.Line.Start == v) || (ls.Line.End == v))
										{
											vvalid = false;
											break;
										}
									}

									// Valid vertex?
									if(vvalid) foundv = v;
								}
							}
						}
					}
				}

				// Found a vertex inside the polygon?
				if(foundv != null)
				{
					// Find the attached linedef with the smallest angle to the right
					float targetangle = Angle2D.PIHALF;
					foundline = null;
					foreach(Linedef l in foundv.Linedefs)
					{
						// We need an angle unrelated to line direction, so correct for that
						float lineangle = l.Angle;
						if(l.End == foundv) lineangle += Angle2D.PI;

						// Better result?
						float deltaangle = Angle2D.Difference(targetangle, lineangle);
						if((foundline == null) || (deltaangle < foundangle))
						{
							foundline = l;
							foundangle = deltaangle;
						}
					}

					// We already know that each linedef will go from this vertex
					// to the left, because this is the right-most vertex in this area.
					// If the line would go to the right, that means the other vertex of
					// that line must lie outside this area and the mapper made an error.
					// Should I check for this error and fail to create a sector in
					// that case or ignore it and create a malformed sector (possibly
					// breaking another sector also)?

					// Find the side at which to start pathfinding
					Vector2D testpos = new Vector2D(100.0f, 0.0f);
					foundlinefront = (foundline.SideOfLine(foundv.Position + testpos) < 0.0f);

					// Find inner path
					List<LinedefSide> innerlines = FindClosestPath(foundline, foundlinefront, true);
					if(innerlines != null)
					{
						// Make polygon
						LinedefTracePath tracepath = new LinedefTracePath(innerlines);
						EarClipPolygon innerpoly = tracepath.MakePolygon(true);

						// Check if the front of the line is outside the polygon
						if(!innerpoly.Intersect(foundline.GetSidePoint(foundlinefront)))
						{
							// Valid hole found!
							alllines.AddRange(innerlines);
							p.InsertChild(innerpoly);
							findmore = true;
						}
					}
				}
			}
			// Continue until no more holes found
			while(findmore);
		}

		// This finds the outer lines of the sector as a polygon
		// Returns null when no valid outer polygon can be found
		private static EarClipPolygon FindOuterLines(Linedef line, bool front, List<LinedefSide> alllines)
		{
			Linedef scanline = line;
			bool scanfront = front;

			do
			{
				// Find closest path
				List<LinedefSide> pathlines = FindClosestPath(scanline, scanfront, true);
				if(pathlines != null)
				{
					// Make polygon
					LinedefTracePath tracepath = new LinedefTracePath(pathlines);
					EarClipPolygon poly = tracepath.MakePolygon(true);

					// Check if the front of the line is inside the polygon
					if(poly.Intersect(line.GetSidePoint(front)))
					{
						// Outer lines found!
						alllines.AddRange(pathlines);
						return poly;
					}
					else
					{
						// Inner lines found. This is not what we need, we want the outer lines.
						// Find the right-most vertex to start a scan from there towards the outer lines.
						Vertex foundv = null;
						foreach(LinedefSide ls in pathlines)
						{
							if((foundv == null) || (ls.Line.Start.Position.x > foundv.Position.x))
								foundv = ls.Line.Start;
							
							if((foundv == null) || (ls.Line.End.Position.x > foundv.Position.x))
								foundv = ls.Line.End;
						}

						// If foundv is null then something is horribly wrong with the
						// path we received from FindClosestPath!
						if(foundv == null) throw new Exception("FAIL!");
						
						// From the right-most vertex trace outward to the right to
						// find the next closest linedef, this is based on the idea that
						// all sectors are closed.
						Vector2D lineoffset = new Vector2D(100.0f, 0.0f);
						Line2D testline = new Line2D(foundv.Position, foundv.Position + lineoffset);
						scanline = null;
						float foundu = float.MaxValue;
						foreach(Linedef ld in General.Map.Map.Linedefs)
						{
							// Line to the right of start point?
							if((ld.Start.Position.x > foundv.Position.x) ||
							   (ld.End.Position.x > foundv.Position.x))
							{
								// Line intersecting the y axis?
								if( !((ld.Start.Position.y > foundv.Position.y) &&
									  (ld.End.Position.y > foundv.Position.y)) &&
								    !((ld.Start.Position.y < foundv.Position.y) &&
									  (ld.End.Position.y < foundv.Position.y)))
								{
									// Check if this linedef intersects our test line at a closer range
									float thisu;
									ld.Line.GetIntersection(testline, out thisu);
									if((thisu > 0.00001f) && (thisu < foundu) && !float.IsNaN(thisu))
									{
										scanline = ld;
										foundu = thisu;
									}
								}
							}
						}

						// Did we meet another line?
						if(scanline != null)
						{
							// Determine on which side we should start the next pathfind
							scanfront = (scanline.SideOfLine(foundv.Position) < 0.0f);
						}
						else
						{
							// Appearently we reached the end of the map, no sector possible here
							return null;
						}
					}
				}
				else
				{
					// Can't find a path
					return null;
				}
			}
			while(true);
		}

		/// <summary>
		/// This finds the closest path from one vertex to another.
		/// When turnatends is true, the algorithm will continue at the other side of the
		/// line when a dead end has been reached. Returns null when no path could be found.
		/// </summary>
		//public static List<LinedefSide> FindClosestPath(Vertex start, float startangle, Vertex end, bool turnatends)
		//{

		//}

		/// <summary>
		/// This finds the closest path from the beginning of a line to the end of the line.
		/// When turnatends is true, the algorithm will continue at the other side of the
		/// line when a dead end has been reached. Returns null when no path could be found.
		/// </summary>
		public static List<LinedefSide> FindClosestPath(Linedef startline, bool startfront, bool turnatends)
		{
			return FindClosestPath(startline, startfront, startline, startfront, turnatends);
		}
		
		/// <summary>
		/// This finds the closest path from the beginning of a line to the end of the line.
		/// When turnatends is true, the algorithm will continue at the other side of the
		/// line when a dead end has been reached. Returns null when no path could be found.
		/// </summary>
		public static List<LinedefSide> FindClosestPath(Linedef startline, bool startfront, Linedef endline, bool endfront, bool turnatends)
		{
			List<LinedefSide> path = new List<LinedefSide>();
			Dictionary<Linedef, int> tracecount = new Dictionary<Linedef, int>();
			Linedef nextline = startline;
			bool nextfront = startfront;

			do
			{
				// Add line to path
				path.Add(new LinedefSide(nextline, nextfront));
				if(!tracecount.ContainsKey(nextline)) tracecount.Add(nextline, 1); else tracecount[nextline]++;

				// Determine next vertex to use
				Vertex v = nextfront ? nextline.End : nextline.Start;

				// Get list of linedefs and sort by angle
				List<Linedef> lines = new List<Linedef>(v.Linedefs);
				LinedefAngleSorter sorter = new LinedefAngleSorter(nextline, nextfront, v);
				lines.Sort(sorter);

				// Source line is the only one?
				if(lines.Count == 1)
				{
					// Are we allowed to trace along this line again?
					if(turnatends && (!tracecount.ContainsKey(nextline) || (tracecount[nextline] < 3)))
					{
						// Turn around and go back along the other side of the line
						nextfront = !nextfront;
					}
					else
					{
						// No more lines, trace ends here
						path = null;
					}
				}
				else
				{
					// Trace along the next line
					Linedef prevline = nextline;
					if(lines[0] == nextline) nextline = lines[1]; else nextline = lines[0];

					// Are we allowed to trace this line again?
					if(!tracecount.ContainsKey(nextline) || (tracecount[nextline] < 3))
					{
						// Check if front side changes
						if((prevline.Start == nextline.Start) ||
						   (prevline.End == nextline.End)) nextfront = !nextfront;
					}
					else
					{
						// No more lines, trace ends here
						path = null;
					}
				}
			}
			// Continue as long as we have not reached the start yet
			// or we have no next line to trace
			while((path != null) && ((nextline != endline) || (nextfront != endfront)));

			// If start and front are not the same, add the end to the list also
			if((path != null) && ((startline != endline) || (startfront != endfront)))
				path.Add(new LinedefSide(endline, endfront));
			
			// Return path (null when trace failed)
			return path;
		}

		#endregion
		
		#region ================== Sector Making

		// This makes the sector from the given lines and sides
		public static Sector MakeSector(List<LinedefSide> alllines)
		{
			Sector newsector = General.Map.Map.CreateSector();
			Sector sourcesector = null;
			SidedefSettings sourceside = new SidedefSettings();
			bool removeuselessmiddle;
			
			// Check if any of the sides already has a sidedef
			// Then we use information from that sidedef to make the others
			foreach(LinedefSide ls in alllines)
			{
				if(ls.Front)
				{
					if(ls.Line.Front != null)
					{
						// Copy sidedef information if not already found
						if(sourcesector == null) sourcesector = ls.Line.Front.Sector;
						TakeSidedefSettings(ref sourceside, ls.Line.Front);
						break;
					}
				}
				else
				{
					if(ls.Line.Back != null)
					{
						// Copy sidedef information if not already found
						if(sourcesector == null) sourcesector = ls.Line.Back.Sector;
						TakeSidedefSettings(ref sourceside, ls.Line.Back);
						break;
					}
				}
			}

			// Now do the same for the other sides
			// Note how information is only copied when not already found
			// so this won't override information from the sides searched above
			foreach(LinedefSide ls in alllines)
			{
				if(ls.Front)
				{
					if(ls.Line.Back != null)
					{
						// Copy sidedef information if not already found
						if(sourcesector == null) sourcesector = ls.Line.Back.Sector;
						TakeSidedefSettings(ref sourceside, ls.Line.Back);
						break;
					}
				}
				else
				{
					if(ls.Line.Front != null)
					{
						// Copy sidedef information if not already found
						if(sourcesector == null) sourcesector = ls.Line.Front.Sector;
						TakeSidedefSettings(ref sourceside, ls.Line.Front);
						break;
					}
				}
			}
			
			// Use defaults where no settings could be found
			TakeSidedefDefaults(ref sourceside);
			
			// Found a source sector?
			if(sourcesector != null)
			{
				// Copy properties from source to new sector
				sourcesector.CopyPropertiesTo(newsector);
			}
			else
			{
				// No source sector, apply default sector properties
				ApplyDefaultsToSector(newsector);
			}

			// Go for all sides to make sidedefs
			foreach(LinedefSide ls in alllines)
			{
				// We may only remove a useless middle texture when
				// the line was previously singlesided
				removeuselessmiddle = (ls.Line.Back == null) || (ls.Line.Front == null);
				
				if(ls.Front)
				{
					// Create sidedef is needed and ensure it points to the new sector
					if(ls.Line.Front == null) General.Map.Map.CreateSidedef(ls.Line, true, newsector);
					if(ls.Line.Front.Sector != newsector) ls.Line.Front.ChangeSector(newsector);
					ApplyDefaultsToSidedef(ls.Line.Front, sourceside);
				}
				else
				{
					// Create sidedef is needed and ensure it points to the new sector
					if(ls.Line.Back == null) General.Map.Map.CreateSidedef(ls.Line, false, newsector);
					if(ls.Line.Back.Sector != newsector) ls.Line.Back.ChangeSector(newsector);
					ApplyDefaultsToSidedef(ls.Line.Back, sourceside);
				}

				// Update line
				if(ls.Line.Front != null) ls.Line.Front.RemoveUnneededTextures(removeuselessmiddle);
				if(ls.Line.Back != null) ls.Line.Back.RemoveUnneededTextures(removeuselessmiddle);
				ls.Line.ApplySidedFlags();
			}

			// Return the new sector
			return newsector;
		}


		// This joins a sector with the given lines and sides
		public static Sector JoinSector(List<LinedefSide> alllines, Sidedef original)
		{
			SidedefSettings sourceside = new SidedefSettings();
			
			// Take settings fro mthe original side
			TakeSidedefSettings(ref sourceside, original);

			// Use defaults where no settings could be found
			TakeSidedefDefaults(ref sourceside);

			// Go for all sides to make sidedefs
			foreach(LinedefSide ls in alllines)
			{
				if(ls.Front)
				{
					// Create sidedef if needed
					if(ls.Line.Front == null)
					{
						General.Map.Map.CreateSidedef(ls.Line, true, original.Sector);
						ApplyDefaultsToSidedef(ls.Line.Front, sourceside);
					}
					// Added 23-9-08, can we do this or will it break things?
					else
					{
						// Link to the new sector
						ls.Line.Front.ChangeSector(original.Sector);
					}
				}
				else
				{
					// Create sidedef if needed
					if(ls.Line.Back == null)
					{
						General.Map.Map.CreateSidedef(ls.Line, false, original.Sector);
						ApplyDefaultsToSidedef(ls.Line.Back, sourceside);
					}
					// Added 23-9-08, can we do this or will it break things?
					else
					{
						// Link to the new sector
						ls.Line.Back.ChangeSector(original.Sector);
					}
				}

				// Update line
				ls.Line.ApplySidedFlags();
			}

			// Return the new sector
			return original.Sector;
		}

		// This takes default settings if not taken yet
		private static void TakeSidedefDefaults(ref SidedefSettings settings)
		{
			// Use defaults where no settings could be found
			if(settings.newtexhigh == null) settings.newtexhigh = General.Settings.DefaultTexture;
			if(settings.newtexmid == null) settings.newtexmid = General.Settings.DefaultTexture;
			if(settings.newtexlow == null) settings.newtexlow = General.Settings.DefaultTexture;
		}

		// This takes sidedef settings if not taken yet
		private static void TakeSidedefSettings(ref SidedefSettings settings, Sidedef side)
		{
			if((side.LongHighTexture != MapSet.EmptyLongName) && (settings.newtexhigh == null))
				settings.newtexhigh = side.HighTexture;
			if((side.LongMiddleTexture != MapSet.EmptyLongName) && (settings.newtexmid == null))
				settings.newtexmid = side.MiddleTexture;
			if((side.LongLowTexture != MapSet.EmptyLongName) && (settings.newtexlow == null))
				settings.newtexlow = side.LowTexture;
		}
		
		// This applies defaults to a sidedef
		private static void ApplyDefaultsToSidedef(Sidedef sd, SidedefSettings defaults)
		{
			if(sd.HighRequired() && sd.HighTexture.StartsWith("-")) sd.SetTextureHigh(defaults.newtexhigh);
			if(sd.MiddleRequired() && sd.MiddleTexture.StartsWith("-")) sd.SetTextureMid(defaults.newtexmid);
			if(sd.LowRequired() && sd.LowTexture.StartsWith("-")) sd.SetTextureLow(defaults.newtexlow);
		}

		// This applies defaults to a sector
		private static void ApplyDefaultsToSector(Sector s)
		{
			s.SetFloorTexture(General.Settings.DefaultFloorTexture);
			s.SetCeilTexture(General.Settings.DefaultCeilingTexture);
			s.FloorHeight = General.Settings.DefaultFloorHeight;
			s.CeilHeight = General.Settings.DefaultCeilingHeight;
			s.Brightness = General.Settings.DefaultBrightness;
		}
		
		#endregion
		
		#region ================== Sector Labels
		
		// This finds the ideal label positions for a sector
		public static List<LabelPositionInfo> FindLabelPositions(Sector s)
		{
			List<LabelPositionInfo> positions = new List<LabelPositionInfo>(2);
			int islandoffset = 0;
			
			// Do we have a triangulation?
			Triangulation triangles = s.Triangles;
			if(triangles != null)
			{
				// Go for all islands
				for(int i = 0; i < triangles.IslandVertices.Count; i++)
				{
					Dictionary<Sidedef, Linedef> sides = new Dictionary<Sidedef, Linedef>(triangles.IslandVertices[i] >> 1);
					List<Vector2D> candidatepositions = new List<Vector2D>(triangles.IslandVertices[i] >> 1);
					float founddistance = float.MinValue;
					Vector2D foundposition = new Vector2D();
					float minx = float.MaxValue;
					float miny = float.MaxValue;
					float maxx = float.MinValue;
					float maxy = float.MinValue;
					
					// Make candidate lines that are not along sidedefs
					// We do this before testing the candidate against the sidedefs so that
					// we can collect the relevant sidedefs first in the same run
					for(int t = 0; t < triangles.IslandVertices[i]; t += 3)
					{
						int triangleoffset = islandoffset + t;
						Vector2D v1 = triangles.Vertices[triangleoffset + 2];
						Sidedef sd = triangles.Sidedefs[triangleoffset + 2];
						for(int v = 0; v < 3; v++)
						{
							Vector2D v2 = triangles.Vertices[triangleoffset + v];
							
							// Not along a sidedef? Then this line is across the sector
							// and guaranteed to be inside the sector!
							if(sd == null)
							{
								// Make the line
								candidatepositions.Add(v1 + (v2 - v1) * 0.5f);
							}
							else
							{
								// This sidedefs is part of this island and must be checked
								// so add it to the dictionary
								sides[sd] = sd.Line;
							}
							
							// Make bbox of this island
							minx = Math.Min(minx, v1.x);
							miny = Math.Min(miny, v1.y);
							maxx = Math.Max(maxx, v1.x);
							maxy = Math.Max(maxy, v1.y);
							
							// Next
							sd = triangles.Sidedefs[triangleoffset + v];
							v1 = v2;
						}
					}

					// Any candidate lines found at all?
					if(candidatepositions.Count > 0)
					{
						// Start with the first line
						foreach(Vector2D candidatepos in candidatepositions)
						{
							// Check distance against other lines
							float smallestdist = int.MaxValue;
							foreach(KeyValuePair<Sidedef, Linedef> sd in sides)
							{
								// Check the distance
								float distance = sd.Value.DistanceToSq(candidatepos, true);
								smallestdist = Math.Min(smallestdist, distance);
							}
							
							// Keep this candidate if it is better than previous
							if(smallestdist > founddistance)
							{
								foundposition = candidatepos;
								founddistance = smallestdist;
							}
						}
						
						// No cceptable line found, just use the first!
						positions.Add(new LabelPositionInfo(foundposition, (float)Math.Sqrt(founddistance)));
					}
					else
					{
						// No candidate lines found.
						
						// Check to see if the island is a triangle
						if(triangles.IslandVertices[i] == 3)
						{
							// Use the center of the triangle
							// TODO: Use the 'incenter' instead, see http://mathworld.wolfram.com/Incenter.html
							Vector2D v = (triangles.Vertices[islandoffset] + triangles.Vertices[islandoffset + 1] + triangles.Vertices[islandoffset + 2]) / 3.0f;
							float d = Line2D.GetDistanceToLineSq(triangles.Vertices[islandoffset], triangles.Vertices[islandoffset + 1], v, false);
							d = Math.Min(d, Line2D.GetDistanceToLineSq(triangles.Vertices[islandoffset + 1], triangles.Vertices[islandoffset + 2], v, false));
							d = Math.Min(d, Line2D.GetDistanceToLineSq(triangles.Vertices[islandoffset + 2], triangles.Vertices[islandoffset], v, false));
							positions.Add(new LabelPositionInfo(v, (float)Math.Sqrt(d)));
						}
						else
						{
							// Use the center of this island.
							float d = Math.Min((maxx - minx) * 0.5f, (maxy - miny) * 0.5f);
							positions.Add(new LabelPositionInfo(new Vector2D(minx + (maxx - minx) * 0.5f, miny + (maxy - miny) * 0.5f), d));
						}
					}
					
					// Done with this island
					islandoffset += triangles.IslandVertices[i];
				}
			}
			else
			{
				// No triangulation was made. FAIL!
				General.Fail("No triangulation exists for sector " + s + " Triangulation is required to create label positions for a sector.");
			}
			
			// Done
			return positions;
		}
		
		#endregion

		#region ================== Drawing
		
		/// <summary>
		/// This draws lines with the given points. Note that this tool removes any existing geometry
		/// marks and marks the new lines and vertices when done.
		/// </summary>
		public static void DrawLines(IList<DrawnVertex> points)
		{
			List<Vertex> newverts = new List<Vertex>();
			List<Vertex> intersectverts = new List<Vertex>();
			List<Linedef> newlines = new List<Linedef>();
			List<Linedef> oldlines = new List<Linedef>(General.Map.Map.Linedefs);
			List<Sidedef> insidesides = new List<Sidedef>();
			List<Vertex> mergeverts = new List<Vertex>();
			List<Vertex> nonmergeverts = new List<Vertex>(General.Map.Map.Vertices);
			MapSet map = General.Map.Map;

			General.Map.Map.ClearAllMarks(false);
			
			// Any points to do?
			if(points.Count > 0)
			{
				/***************************************************\
					STEP 1: Create the new geometry
				\***************************************************/

				// Make first vertex
				Vertex v1 = map.CreateVertex(points[0].pos);
				v1.Marked = true;

				// Keep references
				newverts.Add(v1);
				if(points[0].stitch) mergeverts.Add(v1); else nonmergeverts.Add(v1);

				// Go for all other points
				for(int i = 1; i < points.Count; i++)
				{
					// Create vertex for point
					Vertex v2 = map.CreateVertex(points[i].pos);
					v2.Marked = true;

					// Keep references
					newverts.Add(v2);
					if(points[i].stitch) mergeverts.Add(v2); else nonmergeverts.Add(v2);

					// Create line between point and previous
					Linedef ld = map.CreateLinedef(v1, v2);
					ld.Marked = true;
					ld.ApplySidedFlags();
					ld.UpdateCache();
					newlines.Add(ld);

					// Should we split this line to merge with intersecting lines?
					if(points[i - 1].stitchline && points[i].stitchline)
					{
						// Check if any other lines intersect this line
						List<float> intersections = new List<float>();
						Line2D measureline = ld.Line;
						foreach(Linedef ld2 in map.Linedefs)
						{
							// Intersecting?
							// We only keep the unit length from the start of the line and
							// do the real splitting later, when all intersections are known
							float u;
							if(ld2.Line.GetIntersection(measureline, out u))
							{
								if(!float.IsNaN(u) && (u > 0.0f) && (u < 1.0f) && (ld2 != ld))
									intersections.Add(u);
							}
						}

						// Sort the intersections
						intersections.Sort();

						// Go for all found intersections
						Linedef splitline = ld;
						foreach(float u in intersections)
						{
							// Calculate exact coordinates where to split
							// We use measureline for this, because the original line
							// may already have changed in length due to a previous split
							Vector2D splitpoint = measureline.GetCoordinatesAt(u);

							// Make the vertex
							Vertex splitvertex = map.CreateVertex(splitpoint);
							splitvertex.Marked = true;
							newverts.Add(splitvertex);
							mergeverts.Add(splitvertex);			// <-- add to merge?
							intersectverts.Add(splitvertex);

							// The Split method ties the end of the original line to the given
							// vertex and starts a new line at the given vertex, so continue
							// splitting with the new line, because the intersections are sorted
							// from low to high (beginning at the original line start)
							splitline = splitline.Split(splitvertex);
							splitline.ApplySidedFlags();
							newlines.Add(splitline);
						}
					}

					// Next
					v1 = v2;
				}

				// Join merge vertices so that overlapping vertices in the draw become one.
				MapSet.JoinVertices(mergeverts, mergeverts, false, MapSet.STITCH_DISTANCE);

				// We prefer a closed polygon, because then we can determine the interior properly
				// Check if the two ends of the polygon are closed
				bool drawingclosed = false;
				if(newlines.Count > 0)
				{
					// When not closed, we will try to find a path to close it
					Linedef firstline = newlines[0];
					Linedef lastline = newlines[newlines.Count - 1];
					drawingclosed = (firstline.Start == lastline.End);
					if(!drawingclosed)
					{
						// First and last vertex stitch with geometry?
						if(points[0].stitch && points[points.Count - 1].stitch)
						{
							// Find out where they will stitch
							Linedef l1 = MapSet.NearestLinedefRange(oldlines, firstline.Start.Position, MapSet.STITCH_DISTANCE);
							Linedef l2 = MapSet.NearestLinedefRange(oldlines, lastline.End.Position, MapSet.STITCH_DISTANCE);
							if((l1 != null) && (l2 != null))
							{
								List<LinedefSide> shortestpath = null;

								// Same line?
								if(l1 == l2)
								{
									// Then just connect the two
									shortestpath = new List<LinedefSide>();
									shortestpath.Add(new LinedefSide(l1, true));
								}
								else
								{
									// Find the shortest, closest path between these lines
									List<List<LinedefSide>> paths = new List<List<LinedefSide>>(8);
									paths.Add(Tools.FindClosestPath(l1, true, l2, true, true));
									paths.Add(Tools.FindClosestPath(l1, true, l2, false, true));
									paths.Add(Tools.FindClosestPath(l1, false, l2, true, true));
									paths.Add(Tools.FindClosestPath(l1, false, l2, false, true));
									paths.Add(Tools.FindClosestPath(l2, true, l1, true, true));
									paths.Add(Tools.FindClosestPath(l2, true, l1, false, true));
									paths.Add(Tools.FindClosestPath(l2, false, l1, true, true));
									paths.Add(Tools.FindClosestPath(l2, false, l1, false, true));

									foreach(List<LinedefSide> p in paths)
										if((p != null) && ((shortestpath == null) || (p.Count < shortestpath.Count))) shortestpath = p;
								}

								// Found a path?
								if(shortestpath != null)
								{
									// Check which direction the path goes in
									if(shortestpath[0].Line == l1)
									{
										// Begin at start
										v1 = firstline.Start;
									}
									else
									{
										// Begin at end
										v1 = lastline.End;
									}

									// Go for all vertices in the path to make additional lines
									for(int i = 1; i < shortestpath.Count; i++)
									{
										// Get the next position
										Vector2D v2pos = shortestpath[i].Front ? shortestpath[i].Line.Start.Position : shortestpath[i].Line.End.Position;

										// Make the new vertex
										Vertex v2 = map.CreateVertex(v2pos);
										v2.Marked = true;
										mergeverts.Add(v2);

										// Make the line
										Linedef ld = map.CreateLinedef(v1, v2);
										ld.Marked = true;
										ld.ApplySidedFlags();
										ld.UpdateCache();
										newlines.Add(ld);

										// Next
										v1 = v2;
									}

									// Make the final line
									Linedef lld;

									// Check which direction the path goes in
									if(shortestpath[0].Line == l1)
									{
										// Path stops at end
										lld = map.CreateLinedef(v1, lastline.End);
									}
									else
									{
										// Path stops at begin
										lld = map.CreateLinedef(v1, firstline.Start);
									}

									// Setup line
									lld.Marked = true;
									lld.ApplySidedFlags();
									lld.UpdateCache();
									newlines.Add(lld);

									// Drawing is now closed
									drawingclosed = true;

									// Join merge vertices so that overlapping vertices in the draw become one.
									MapSet.JoinVertices(mergeverts, mergeverts, false, MapSet.STITCH_DISTANCE);
								}
							}
						}
					}
				}

				// Merge intersetion vertices with the new lines. This completes the
				// self intersections for which splits were made above.
				map.Update(true, false);
				MapSet.SplitLinesByVertices(newlines, intersectverts, MapSet.STITCH_DISTANCE, null);
				MapSet.SplitLinesByVertices(newlines, mergeverts, MapSet.STITCH_DISTANCE, null);

				/***************************************************\
					STEP 2: Merge the new geometry
				\***************************************************/

				// In step 3 we will make sectors on the front sides and join sectors on the
				// back sides, but because the user could have drawn counterclockwise or just
				// some weird polygon this could result in problems. The following code adjusts
				// the direction of all new lines so that their front (right) side is facing
				// the interior of the new drawn polygon.
				map.Update(true, false);
				foreach(Linedef ld in newlines)
				{
					// Find closest path starting with the front of this linedef
					List<LinedefSide> pathlines = Tools.FindClosestPath(ld, true, true);
					if(pathlines != null)
					{
						// Make polygon
						LinedefTracePath tracepath = new LinedefTracePath(pathlines);
						EarClipPolygon pathpoly = tracepath.MakePolygon(true);

						// Check if the front of the line is outside the polygon
						if(!pathpoly.Intersect(ld.GetSidePoint(true)))
						{
							// Now trace from the back side of the line to see if
							// the back side lies in the interior. I don't want to
							// flip the line if it is not helping.

							// Find closest path starting with the back of this linedef
							pathlines = Tools.FindClosestPath(ld, false, true);
							if(pathlines != null)
							{
								// Make polygon
								tracepath = new LinedefTracePath(pathlines);
								pathpoly = tracepath.MakePolygon(true);

								// Check if the back of the line is inside the polygon
								if(pathpoly.Intersect(ld.GetSidePoint(false)))
								{
									// We must flip this linedef to face the interior
									ld.FlipVertices();
									ld.FlipSidedefs();
									ld.UpdateCache();
								}
							}
						}
					}
				}

				// Mark only the vertices that should be merged
				map.ClearMarkedVertices(false);
				foreach(Vertex v in mergeverts) v.Marked = true;

				// Before this point, the new geometry is not linked with the existing geometry.
				// Now perform standard geometry stitching to merge the new geometry with the rest
				// of the map. The marked vertices indicate the new geometry.
				map.StitchGeometry();
				map.Update(true, false);

				// Find our new lines again, because they have been merged with the other geometry
				// but their Marked property is copied where they have joined.
				newlines = map.GetMarkedLinedefs(true);

				/***************************************************\
					STEP 3: Join and create new sectors
				\***************************************************/

				// The code below atempts to create sectors on the front sides of the drawn
				// geometry and joins sectors on the back sides of the drawn geometry.
				// This code does not change any geometry, it only makes/updates sidedefs.
				bool sidescreated = false;
				bool[] frontsdone = new bool[newlines.Count];
				bool[] backsdone = new bool[newlines.Count];
				for(int i = 0; i < newlines.Count; i++)
				{
					Linedef ld = newlines[i];

					// Front not marked as done?
					if(!frontsdone[i])
					{
						// Find a way to create a sector here
						List<LinedefSide> sectorlines = Tools.FindPotentialSectorAt(ld, true);
						if(sectorlines != null)
						{
							sidescreated = true;

							// Make the new sector
							Sector newsector = Tools.MakeSector(sectorlines);

							// Go for all sidedefs in this new sector
							foreach(Sidedef sd in newsector.Sidedefs)
							{
								// Keep list of sides inside created sectors
								insidesides.Add(sd);

								// Side matches with a side of our new lines?
								int lineindex = newlines.IndexOf(sd.Line);
								if(lineindex > -1)
								{
									// Mark this side as done
									if(sd.IsFront)
										frontsdone[lineindex] = true;
									else
										backsdone[lineindex] = true;
								}
							}
						}
					}

					// Back not marked as done?
					if(!backsdone[i])
					{
						// Find a way to create a sector here
						List<LinedefSide> sectorlines = Tools.FindPotentialSectorAt(ld, false);
						if(sectorlines != null)
						{
							// We don't always want to create a new sector on the back sides
							// So first check if any of the surrounding lines originally have sidedefs
							Sidedef joinsidedef = null;
							foreach(LinedefSide ls in sectorlines)
							{
								if(ls.Front && (ls.Line.Front != null))
								{
									joinsidedef = ls.Line.Front;
									break;
								}
								else if(!ls.Front && (ls.Line.Back != null))
								{
									joinsidedef = ls.Line.Back;
									break;
								}
							}

							// Join?
							if(joinsidedef != null)
							{
								sidescreated = true;

								// Join the new sector
								Sector newsector = Tools.JoinSector(sectorlines, joinsidedef);

								// Go for all sidedefs in this new sector
								foreach(Sidedef sd in newsector.Sidedefs)
								{
									// Side matches with a side of our new lines?
									int lineindex = newlines.IndexOf(sd.Line);
									if(lineindex > -1)
									{
										// Mark this side as done
										if(sd.IsFront)
											frontsdone[lineindex] = true;
										else
											backsdone[lineindex] = true;
									}
								}
							}
						}
					}
				}

				// Make corrections for backward linedefs
				MapSet.FlipBackwardLinedefs(newlines);

				// Remove all unneeded textures
				// Shouldn't this already be done by the
				// makesector/joinsector functions?
				foreach(Linedef ld in newlines)
				{
					if(ld.Front != null) ld.Front.RemoveUnneededTextures(true);
					if(ld.Back != null) ld.Back.RemoveUnneededTextures(true);
				}
				foreach(Sidedef sd in insidesides)
				{
					sd.RemoveUnneededTextures(true);
				}

				// Check if any of our new lines have sides
				if(sidescreated)
				{
					// Then remove the lines which have no sides at all
					for(int i = newlines.Count - 1; i >= 0; i--)
					{
						// Remove the line if it has no sides
						if((newlines[i].Front == null) && (newlines[i].Back == null)) newlines[i].Dispose();
					}
				}
				
				// Snap to map format accuracy
				General.Map.Map.SnapAllToAccuracy();

				// Mark new geometry only
				General.Map.Map.ClearAllMarks(false);
				foreach(Vertex v in newverts) v.Marked = true;
				foreach(Linedef l in newlines) l.Marked = true;
			}
		}
		
		#endregion
		
		#region ================== Texture Alignment

		// This performs texture alignment along all walls that match with the same texture
		// NOTE: This method uses the sidedefs marking to indicate which sides have been aligned
		// When resetsidemarks is set to true, all sidedefs will first be marked false (not aligned).
		// Setting resetsidemarks to false is usefull to align only within a specific selection
		// (set the marked property to true for the sidedefs outside the selection)
		public static void AutoAlignTextures(Sidedef start, long texturelongname, bool alignx, bool aligny, bool resetsidemarks)
		{
			Stack<SidedefAlignJob> todo = new Stack<SidedefAlignJob>(50);
			
			// Mark all sidedefs false (they will be marked true when the texture is aligned)
			if(resetsidemarks) General.Map.Map.ClearMarkedSidedefs(false);
			
			// Begin with first sidedef
			SidedefAlignJob first = new SidedefAlignJob();
			first.sidedef = start;
			first.offsetx = start.OffsetX;
			first.offsety = start.OffsetY + start.Sector.CeilHeight;
			first.forward = true;
			todo.Push(first);
			
			// Continue until nothing more to align
			while(todo.Count > 0)
			{
				// Get the align job to do
				SidedefAlignJob j = todo.Pop();
				
				if(j.forward)
				{
					Vertex v;
					
					// Apply alignment
					if(alignx) j.sidedef.OffsetX = j.offsetx;
					if(aligny) j.sidedef.OffsetY = j.offsety - j.sidedef.Sector.CeilHeight;
					int forwardoffset = j.offsetx + (int)Math.Round(j.sidedef.Line.Length);
					int backwardoffset = j.offsetx;
					j.sidedef.Marked = true;
					
					// Add sidedefs forward (connected to the right vertex)
					v = j.sidedef.IsFront ? j.sidedef.Line.End : j.sidedef.Line.Start;
					AddSidedefsForAlignment(todo, v, true, forwardoffset, j.offsety, texturelongname);

					// Add sidedefs backward (connected to the left vertex)
					v = j.sidedef.IsFront ? j.sidedef.Line.Start : j.sidedef.Line.End;
					AddSidedefsForAlignment(todo, v, false, backwardoffset, j.offsety, texturelongname);
				}
				else
				{
					Vertex v;

					// Apply alignment
					if(alignx) j.sidedef.OffsetX = j.offsetx - (int)Math.Round(j.sidedef.Line.Length);
					if(aligny) j.sidedef.OffsetY = j.offsety - j.sidedef.Sector.CeilHeight;
					int forwardoffset = j.offsetx;
					int backwardoffset = j.offsetx - (int)Math.Round(j.sidedef.Line.Length);
					j.sidedef.Marked = true;

					// Add sidedefs backward (connected to the left vertex)
					v = j.sidedef.IsFront ? j.sidedef.Line.Start : j.sidedef.Line.End;
					AddSidedefsForAlignment(todo, v, false, backwardoffset, j.offsety, texturelongname);

					// Add sidedefs forward (connected to the right vertex)
					v = j.sidedef.IsFront ? j.sidedef.Line.End : j.sidedef.Line.Start;
					AddSidedefsForAlignment(todo, v, true, forwardoffset, j.offsety, texturelongname);
				}
			}
		}

		// This adds the matching, unmarked sidedefs from a vertex for texture alignment
		private static void AddSidedefsForAlignment(Stack<SidedefAlignJob> stack, Vertex v, bool forward, int offsetx, int offsety, long texturelongname)
		{
			foreach(Linedef ld in v.Linedefs)
			{
				Sidedef side1 = forward ? ld.Front : ld.Back;
				Sidedef side2 = forward ? ld.Back : ld.Front;
				if((ld.Start == v) && (side1 != null) && !side1.Marked)
				{
					if(SidedefTextureMatch(side1, texturelongname))
					{
						SidedefAlignJob nj = new SidedefAlignJob();
						nj.forward = forward;
						nj.offsetx = offsetx;
						nj.offsety = offsety;
						nj.sidedef = side1;
						stack.Push(nj);
					}
				}
				else if((ld.End == v) && (side2 != null) && !side2.Marked)
				{
					if(SidedefTextureMatch(side2, texturelongname))
					{
						SidedefAlignJob nj = new SidedefAlignJob();
						nj.forward = forward;
						nj.offsetx = offsetx;
						nj.offsety = offsety;
						nj.sidedef = side2;
						stack.Push(nj);
					}
				}
			}
		}
		
		// This checks if any of the sidedef texture match the given texture
		private static bool SidedefTextureMatch(Sidedef sd, long texturelongname)
		{
			return (sd.LongHighTexture == texturelongname) ||
				   (sd.LongLowTexture == texturelongname) ||
				   (sd.LongMiddleTexture == texturelongname);
		}
		
		#endregion
	}
}
