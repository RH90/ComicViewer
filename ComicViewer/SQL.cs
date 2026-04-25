using ComicViewer.Objects;
using SharpCompress.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Drawing;

//using System.Data.SQLite;
using System.IO;
using System.Windows.Documents;

namespace MangaListWPF
{
    class SQL
    {

        //private string dbfolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Desktop\Programming\db\manga.db";
        SQLiteConnection conMain;
        private string dbpathMain = "";
        public SQL(String dbpath)
        {
            dbpathMain = dbpath;

            if (!File.Exists("dbLocation.txt"))
            {


                if (!File.Exists(dbpath))
                {


                    SQLiteConnection con1 = new SQLiteConnection("Data Source=" + dbpath + ";Version=3;New=True;");
                    con1.Open();

                    SQLiteCommand query = con1.CreateCommand();
                    query.CommandText = "CREATE TABLE IF NOT EXISTS comics(" +
                        "name TEXT PRIMARY KEY UNIQUE NOT NULL, " +
                        "pos INTEGER DEFAULT 0, " +
                        "FitToWindow INTEGER DEFAULT 0, " +
                        "LastOpened INTEGER DEFAULT 0," +
                        "Parent TEXT NOT NULL" +
                        ")";
                    query.ExecuteNonQuery();


                    con1.Dispose();
                }

            }
            conMain = new SQLiteConnection("Data Source=" + dbpathMain + ";Version=3;");
            conMain.Open();

        }

        public SQLiteConnection getCon()
        {
            SQLiteConnection con = new SQLiteConnection("Data Source=" + dbpathMain + ";Version=3;");
            con.Open();
            return con;
        }

        public ComicItem getComic(string name)
        {

            string sql = "SELECT * FROM comics WHERE LOWER(name)=LOWER(@name) LIMIT 1";
            SQLiteCommand query = new SQLiteCommand(sql, conMain);
            query.Parameters.AddWithValue("name", name);
            SQLiteDataReader reader = query.ExecuteReader();
            ComicItem comicItem = new ComicItem();
            if (reader.Read())
            {
                comicItem.Name = reader.GetString(reader.GetOrdinal("name"));
                comicItem.Pos = reader.GetInt32(reader.GetOrdinal("pos"));
                comicItem.FitToWindow = reader.GetInt32(reader.GetOrdinal("FitToWindow")) == 1;
                comicItem.LastOpened = reader.GetInt64(reader.GetOrdinal("LastOpened"));
                comicItem.Parent = reader.GetString(reader.GetOrdinal("Parent"));
                return comicItem;
            }
            else
            {
                return null;
            }


        }
        public void add(ComicItem comicItem)
        {

            string sql = "INSERT OR REPLACE INTO comics(name,pos,FitToWindow,LastOpened,Parent) VALUES(@name,@pos,@FitToWindow,@LastOpened,@Parent)";
            SQLiteCommand query = new SQLiteCommand(sql, conMain);
            query.Parameters.AddWithValue("name", comicItem.Name);
            query.Parameters.AddWithValue("pos", comicItem.Pos);
            query.Parameters.AddWithValue("FitToWindow", comicItem.FitToWindow ? 1 : 0);
            query.Parameters.AddWithValue("LastOpened", comicItem.LastOpened);
            query.Parameters.AddWithValue("Parent", comicItem.Parent);
            query.ExecuteNonQuery();

        }

        public List<ComicItem> getRecentlyOpened()
        {

            string sql = "SELECT * FROM comics ORDER BY LastOpened DESC LIMIT 50";
            SQLiteCommand query = new SQLiteCommand(sql, conMain);
            SQLiteDataReader reader = query.ExecuteReader();
            List<ComicItem> recentList = new List<ComicItem>();
            while (reader.Read())
            {
                ComicItem comicItem = new ComicItem();
                comicItem.Name = reader.GetString(reader.GetOrdinal("name"));
                comicItem.Pos = reader.GetInt32(reader.GetOrdinal("pos"));
                comicItem.FitToWindow = reader.GetInt32(reader.GetOrdinal("FitToWindow")) == 1;
                comicItem.LastOpened = reader.GetInt64(reader.GetOrdinal("LastOpened"));
                comicItem.Parent = reader.GetString(reader.GetOrdinal("Parent"));
                recentList.Add(comicItem);
            }
            return recentList;

        }

        //public void remove(string name)
        //{

        //    string sql = "DELETE FROM chapterList WHERE name=@name";
        //    SQLiteCommand query = new SQLiteCommand(sql, con);
        //    query.Parameters.AddWithValue("name", name);

        //    query.ExecuteNonQuery();


        //    sql = "DELETE FROM mangaList WHERE name=@name";
        //    query = new SQLiteCommand(sql, con);
        //    query.Parameters.AddWithValue("name", name);


        //    query.ExecuteNonQuery();

        //    Console.WriteLine("removed");



        //    sql = "DELETE FROM mangaList WHERE name=@name";
        //    query = new SQLiteCommand(sql, con);
        //    query.Parameters.AddWithValue("name", name);


        //    query.ExecuteNonQuery();

        //    Console.WriteLine("removed");


        //}
    }
}
