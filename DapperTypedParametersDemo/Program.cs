using System;
using System.Data.SqlClient;
using Dapper;
using Faker; //To create vaguely realistic test data
using System.Data;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using static System.Console;

namespace DapperTypedParametersDemo
{
    class Program
    {
        static void Main(string[] args)
        {
            var connectionString = @"Data Source = (LocalDB)\MSSQLLocalDB; AttachDbFilename = C:\Users\ala\Documents\Visual Studio 2017\Projects\DapperTypedParametersDemo\DapperTypedParametersDemo\Data\Contoso.mdf; Integrated Security = True; Connect Timeout = 30;";

            WriteLine("Start benchmark simple and complex way to pass arguments to db using dapper.");
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();

                EmployeeRepository.CreateTable(conn);

                var benchmark = new Benchmark(conn, 100_000);
                var results = benchmark.Run();

                WriteLine($"{"Test",-20} {"Elapsed Time", -20} {"Iterations", -20}");
                foreach (var result in results)
                {
                    WriteLine($"{result.name, -20} {result.elapsed.Seconds + "s",-20} {result.iterations, -20}");
                }
                EmployeeRepository.DropTable(conn);
            }
            ReadLine();
        }
    }

    //Sample domain entity 
    public class Employee
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public DateTime DateOfBirth { get; set; }
        public DateTime DateVested { get; set; }
        public override string ToString() =>
            $"{nameof(Id)}:{Id,6:D6}| {nameof(FirstName)}:{FirstName,-15}|{nameof(LastName)}:{LastName,-15}|{nameof(DateOfBirth)}:{DateOfBirth.ToString("yyyy-MM-dd")}|{nameof(DateVested)}:{DateVested.ToString("yyyy-MM-dd")}";
    }

    public class Benchmark
    {
        readonly SqlConnection conn;
        readonly int iterations;

        public Benchmark(SqlConnection conn, int iterations)
        {
            this.conn = conn;
            this.iterations = iterations;
        }

        public IEnumerable<(string name, int iterations, TimeSpan elapsed)> Run()
        {
            var repository = new EmployeeRepository(conn);

            yield return Run(emp => repository.AddSimple(emp), 
                () => EmployeeRepository.TruncateTable(conn), 
                iterations => GenerateEmployees(iterations), 
                nameof(repository.AddSimple), 
                iterations);

            yield return Run(emp => repository.UpdateSimple(emp), 
                () => { }, 
                iterations => ModifiedEmployees(iterations), 
                nameof(repository.UpdateSimple), 
                iterations);

            yield return Run(emp => repository.AddComplex(emp), 
                () => EmployeeRepository.TruncateTable(conn), 
                iterations => GenerateEmployees(iterations), 
                nameof(repository.AddComplex), 
                iterations);

            yield return Run(emp =>repository.UpdateComplex(emp), 
                () => { }, 
                iterations => ModifiedEmployees(iterations), 
                nameof(repository.UpdateComplex), 
                iterations);
        }

        public (string name, int iterations, TimeSpan elapsed) Run(Action<Employee> mainAction,
            Action resetAction,
            Func<int, IEnumerable<Employee>> dataGenerator,
            string mainActionName,
            int iterations)
        {
            var employees = dataGenerator(iterations);

            //Run actions on some employees to warmup JIT and system
            foreach (var e in employees.Take(1000))
                mainAction(e);
            resetAction();

            GC.Collect();
            GC.WaitForPendingFinalizers();

            var timer = Stopwatch.StartNew();
            foreach (var e in employees)
                mainAction(e);
            timer.Stop();

            //PrintAllEmployeesinDb();

            return (mainActionName, employees.Count(), timer.Elapsed);
        }

        void PrintAllEmployeesinDb()
        {
            var repository = new EmployeeRepository(conn);
            //Get the employees from db
            foreach (var employeeInDb in repository.GetAll())
            {
                WriteLine(employeeInDb);
            }
        }

        private IEnumerable<Employee> GenerateEmployees(int iterations)
        {
            var alice = new Employee { FirstName = "Alice", LastName = "Smith", DateOfBirth = new DateTime(1990, 01, 01), DateVested = new DateTime(2015, 01, 01) };
            yield return alice;

            //Make more random employees
            foreach (var i in Enumerable.Range(1, iterations - 1))
                yield return new Employee
                {
                    FirstName = Name.First(),
                    LastName = Name.Last(),
                    DateOfBirth = alice.DateOfBirth.AddDays(i % 1000),
                    DateVested = alice.DateVested.AddYears(1).AddDays(i % 1000)
                };
        }

        private IEnumerable<Employee> ModifiedEmployees(int iterations)
        {
            var repository = new EmployeeRepository(conn);
            foreach (var e in repository.GetAll())
            {
                e.FirstName = Name.First();
                e.LastName = Name.Last();
                e.DateOfBirth = e.DateOfBirth.AddDays(-7);
                e.DateVested = e.DateVested.AddDays(-7);

                yield return e;
            }
        }
    }

    public class EmployeeRepository
    {

        readonly SqlConnection conn;
        public EmployeeRepository(SqlConnection conn)
        {
            this.conn = conn;
        }

        public IEnumerable<Employee> GetAll()
        {
            var sql =
                "select Id, colFirstName FirstName, colLastName LastName, DateOfBirth, DateVested from Employees ";
            return conn.Query<Employee>(sql);
        }

        //Yes, I know command methods belong in UnitOfWork rather than a Repository
        //Note that .Net parameters type are set to match sql column datatype.
        public int AddComplex(Employee employee)
        {
            var sql = $"insert into Employees (colFirstName, colLastName, DateOfBirth, DateVested) values (@{nameof(Employee.FirstName)}, @{nameof(Employee.LastName)}, @{nameof(Employee.DateOfBirth)}, @{nameof(Employee.DateVested)});" +
                "select cast(scope_identity() as int);";
            var args = new DynamicParameters();
            args.Add(nameof(Employee.FirstName), employee.FirstName, size: 30); //.Net string type defaults DbTpye.String which matches Sql nvarchar
            args.Add(nameof(Employee.LastName), employee.LastName, DbType.AnsiString, size: 30);
            args.Add(nameof(Employee.DateOfBirth), employee.DateOfBirth); //.Net DateTime type defaults to Sql datetime 
            args.Add(nameof(Employee.DateVested), employee.DateVested, DbType.DateTime2);

            return conn.Query<int>(sql, args).SingleOrDefault();
        }

        //Nicer syntax but may be slower and higher CPU because of implicit conversion of .Net string to nvarchar to varchar (on server) and datetime to sql datetime then to datetime2.
        public int AddSimple(Employee employee)
        {
            var sql = $"insert into Employees (colFirstName, colLastName, DateOfBirth, DateVested) values (@{nameof(Employee.FirstName)}, @{nameof(Employee.LastName)}, @{nameof(Employee.DateOfBirth)}, @{nameof(Employee.DateVested)});" +
                "select cast(scope_identity() as int);";

            return conn.Query<int>(sql, employee).SingleOrDefault();
        }

        public int UpdateComplex(Employee employee)
        {
            var sql = $"update Employees set colFirstName = @{nameof(Employee.FirstName)}, colLastName = @{nameof(Employee.LastName)}, DateOfBirth = @{nameof(Employee.DateOfBirth)}, DateVested = @{nameof(Employee.DateVested)} " +
                $"where Id = @{nameof(Employee.Id)};";
            var args = new DynamicParameters();
            args.Add(nameof(Employee.FirstName), employee.FirstName, size: 30); //.Net string type defaults DbTpye.String which matches Sql nvarchar
            args.Add(nameof(Employee.LastName), employee.LastName, DbType.AnsiString, size: 30);
            args.Add(nameof(Employee.DateOfBirth), employee.DateOfBirth); //.Net DateTime type defaults to Sql datetime 
            args.Add(nameof(Employee.DateVested), employee.DateVested, DbType.DateTime2);
            args.Add(nameof(Employee.Id), employee.Id);

            return conn.Execute(sql, args);
        }


        public int UpdateSimple(Employee employee)
        {
            var sql = $"update Employees set colFirstName = @{nameof(Employee.FirstName)}, colLastName = @{nameof(Employee.LastName)}, DateOfBirth = @{nameof(Employee.DateOfBirth)}, DateVested = @{nameof(Employee.DateVested)} " +
                $"where Id = @{nameof(Employee.Id)};";

            return conn.Execute(sql, employee);
        }
        //DDL methods definitely do not belong in a repository
        public static void CreateTable(SqlConnection conn)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                If (Object_Id('Employees') Is Null)
                Begin
	                Create Table Employees
	                (
                        --Yes, inconsistent naming convention
		                Id int identity primary key, 
		                ColFirstName nvarchar(30) not null, 
		                ColLastName varchar(30) not null,
                        DateOfBirth datetime not null,
                        DateVested datetime2 not null --we want nanoseconds accuracy
	                );
                End";
            cmd.ExecuteNonQuery();
        }
        public static void TruncateTable(SqlConnection conn)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                If (Object_Id('Employees') Is Not Null)
                Begin
	                Truncate Table Employees;
                End";
            cmd.ExecuteNonQuery();
        }
        public static void DropTable(SqlConnection conn)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                If (Object_Id('Employees') Is Not Null)
                Begin
	                Drop Table Employees;
                End
                ";
            cmd.ExecuteNonQuery();
        }
    }
}